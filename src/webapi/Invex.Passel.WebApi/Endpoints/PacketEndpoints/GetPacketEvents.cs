using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    /// <summary>
    ///     Sent once the stream is subscribed, and whenever events may have been missed. Packets fetched after receiving
    ///     it are guaranteed not to miss any created later, as those arrive on the stream.
    /// </summary>
    internal const string ResyncEventType = "resync";

    /// <summary>
    ///     A packet was created; the data is the packet in the same shape as a <c>GET /api/packets</c> item.
    /// </summary>
    internal const string PacketCreatedEventType = "packet-created";

    /// <summary>
    ///     A packet was deleted; the data is a <see cref="PacketDeletedEventData" />.
    /// </summary>
    internal const string PacketDeletedEventType = "packet-deleted";

    [PublicAPI]
    public sealed record PacketDeletedEventData
    {
        public required string Id { get; init; }
    }

    /// <summary>
    ///     Keeps idle connections open through proxies and lets clients detect a dead connection.
    /// </summary>
    internal const string HeartbeatEventType = "ping";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     The range of the SSE <c>retry</c> value sent with the first event. It is drawn per stream, so clients that
    ///     honour it reconnect spread over the range after a restart rather than all at once.
    /// </summary>
    internal static readonly TimeSpan MinReconnectionInterval = TimeSpan.FromSeconds(2);

    internal static readonly TimeSpan MaxReconnectionInterval = TimeSpan.FromSeconds(6);

    /// <summary>
    ///     No stream outlives this, whatever its token says; clients reconnect with a fresh token.
    /// </summary>
    internal static readonly TimeSpan MaxStreamLifetime = TimeSpan.FromDays(1);

    /// <summary>
    ///     The JWT expiry claim, in seconds since the Unix epoch. The bearer handler leaves it unmapped.
    /// </summary>
    private const string ExpiryClaim = "exp";

    /// <summary>
    ///     How long a stream opened at <paramref name="now" /> may stay open: until the caller's token would no
    ///     longer validate (its expiry plus the validation clock skew), and at most <see cref="MaxStreamLifetime" />.
    ///     A principal without a usable expiry, such as one from the development schemes, gets the maximum.
    /// </summary>
    internal static TimeSpan StreamLifetime(ClaimsPrincipal user, DateTimeOffset now, TimeSpan clockSkew)
    {
        if (!long.TryParse(user.FindFirstValue(ExpiryClaim),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiresAt))
            return MaxStreamLifetime;

        var remainingSeconds = expiresAt - now.ToUnixTimeSeconds() + clockSkew.TotalSeconds;

        if (remainingSeconds <= 0)
            return TimeSpan.Zero;

        return remainingSeconds >= MaxStreamLifetime.TotalSeconds
            ? MaxStreamLifetime
            : TimeSpan.FromSeconds(remainingSeconds);
    }

    /// <summary>
    ///     A reconnection interval for one stream: whole milliseconds from <see cref="MinReconnectionInterval" /> to
    ///     <see cref="MaxReconnectionInterval" />, both inclusive.
    /// </summary>
    internal static TimeSpan NextReconnectionInterval(Random random) =>
        TimeSpan.FromMilliseconds(random.Next((int)MinReconnectionInterval.TotalMilliseconds,
            (int)MaxReconnectionInterval.TotalMilliseconds + 1));

    /// <summary>
    ///     Yields a resync event, then the events read from <paramref name="reader" />, with a heartbeat after every
    ///     <paramref name="heartbeatInterval" /> without one. Ends when the reader completes or the request is aborted.
    ///     The resync carries <paramref name="reconnectionInterval" /> as the SSE <c>retry</c> value when one is given.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> StreamPacketEvents(
        ChannelReader<PacketEvent> reader,
        Func<Packet, string> serializePacket,
        Func<Guid, string> serializeDeleted,
        TimeSpan heartbeatInterval,
        TimeSpan? reconnectionInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(string.Empty, ResyncEventType)
        {
            ReconnectionInterval = reconnectionInterval,
        };

        // Kept across heartbeats so a single pending wait is never abandoned
        Task<bool>? waitToRead = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            waitToRead ??= reader
                .WaitToReadAsync(cancellationToken)
                .AsTask();

            var canRead = await WaitAsync(waitToRead, heartbeatInterval, cancellationToken);

            if (canRead is null)
            {
                yield return new(string.Empty, HeartbeatEventType);

                continue;
            }

            if (canRead is false)
                yield break;

            waitToRead = null;

            while (reader.TryRead(out var packetEvent))
                yield return packetEvent switch
                {
                    PacketEvent.Created created => new(serializePacket(created.Packet), PacketCreatedEventType),
                    PacketEvent.Deleted deleted => new(serializeDeleted(deleted.PacketId), PacketDeletedEventType),
                    PacketEvent.Resync => new(string.Empty, ResyncEventType),
                    _ => throw new UnreachableException(),
                };
        }
    }

    /// <summary>
    ///     Returns the result of <paramref name="waitToRead" />, <c>null</c> when <paramref name="timeout" /> passes
    ///     first, or <c>false</c> once the stream is cancelled. An idle stream times out on nearly every call, so the
    ///     timeout is a delay that completes rather than an exception that is thrown and caught.
    /// </summary>
    private static async Task<bool?> WaitAsync(
        Task<bool> waitToRead,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Linked to the stream so that cancelling it ends the delay too, and cancelled when an event wins so that the
        // delay releases its timer instead of holding it until the interval is up
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timedOut = Task.Delay(timeout, timer.Token);

        var completed = await Task.WhenAny(waitToRead, timedOut);

        if (cancellationToken.IsCancellationRequested)
            return false;

        if (completed == timedOut)
            return null;

        timer.Cancel();

        return await waitToRead;
    }

    public static void MapGetPacketEvents(this IEndpointRouteBuilder builder) =>
        builder
            .MapGet("/events",
                (
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IPacketEventHub eventHub,
                    IHostApplicationLifetime lifetime,
                    IOptions<HttpJsonOptions> jsonOptions,
                    IOptionsMonitor<JwtBearerOptions> bearerOptions) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    // Subscribed before the first (resync) event is sent, so nothing can fall between the two
                    var subscription = eventHub.Subscribe(ownerId);

                    if (subscription is null)
                    {
                        httpContext.Response.Headers.RetryAfter = "30";

                        return Results.Problem("This server has no room for another event stream. Try again later.",
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "Too many event streams");
                    }

                    // Released when the request ends, which for a stream is when it closes
                    httpContext.Response.RegisterForDispose(subscription);

                    // The stream ends when the token that opened it would stop validating; the clients reconnect
                    // with a fresh one
                    var clockSkew = bearerOptions
                        .Get(JwtBearerDefaults.AuthenticationScheme)
                        .TokenValidationParameters
                        .ClockSkew;

                    var maxAge = StreamLifetime(httpContext.User, DateTimeOffset.UtcNow, clockSkew);

                    // Stops reverse proxies such as nginx from buffering the stream
                    httpContext.Response.Headers["X-Accel-Buffering"] = "no";

                    return TypedResults.ServerSentEvents(Stream(subscription.Reader, maxAge, httpContext.RequestAborted));

                    async IAsyncEnumerable<SseItem<string>> Stream(
                        ChannelReader<PacketEvent> reader,
                        TimeSpan streamMaxAge,
                        [EnumeratorCancellation] CancellationToken cancellationToken = default)
                    {
                        // Ends the stream on shutdown so the server doesn't wait out its shutdown timeout for it;
                        // the client reconnects (to another instance, or this one once restarted)
                        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                            lifetime.ApplicationStopping);

                        stopping.CancelAfter(streamMaxAge);

                        var serializerOptions = jsonOptions.Value.SerializerOptions;

                        await foreach (var item in StreamPacketEvents(reader,
                                           packet => JsonSerializer.Serialize(
                                               ToResponsePacket(packet, encodingService),
                                               serializerOptions),
                                           packetId => JsonSerializer.Serialize(new PacketDeletedEventData
                                               {
                                                   Id = encodingService.Encode(packetId),
                                               },
                                               serializerOptions),
                                           HeartbeatInterval,
                                           NextReconnectionInterval(Random.Shared),
                                           stopping.Token))
                            yield return item;
                    }
                })
            .RequireRateLimiting(RateLimitPolicies.Streams);
}
