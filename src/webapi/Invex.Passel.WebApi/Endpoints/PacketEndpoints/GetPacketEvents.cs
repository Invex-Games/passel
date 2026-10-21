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
    ///     Keeps idle connections open through proxies and lets clients detect a dead connection.
    /// </summary>
    internal const string HeartbeatEventType = "ping";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Yields a resync event, then the events read from <paramref name="reader" />, with a heartbeat after every
    ///     <paramref name="heartbeatInterval" /> without one. Ends when the reader completes or the request is aborted.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> StreamPacketEvents(
        ChannelReader<PacketEvent> reader,
        Func<PacketAggregate, string> serializePacket,
        TimeSpan heartbeatInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(string.Empty, ResyncEventType);

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
                    PacketEvent.Resync => new(string.Empty, ResyncEventType),
                    _ => throw new UnreachableException(),
                };
        }
    }

    /// <summary>
    ///     Returns the result of <paramref name="waitToRead" />, <c>null</c> on timeout, or <c>false</c> if cancelled.
    /// </summary>
    private static async Task<bool?> WaitAsync(
        Task<bool> waitToRead,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waitToRead.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static void MapGetPacketEvents(this IEndpointRouteBuilder builder) =>
        builder.MapGet("/events",
            (
                HttpContext httpContext,
                IIdEncodingService encodingService,
                IPacketEventHub eventHub,
                IHostApplicationLifetime lifetime,
                IOptions<HttpJsonOptions> jsonOptions) =>
            {
                var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (ownerId is not { Length: > 0 })
                    return Results.Unauthorized();

                // Stops reverse proxies such as nginx from buffering the stream
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";

                return TypedResults.ServerSentEvents(Stream(ownerId, httpContext.RequestAborted));

                async IAsyncEnumerable<SseItem<string>> Stream(
                    string userId,
                    [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    // Ends the stream on shutdown so the server doesn't wait out its shutdown timeout for it;
                    // the client reconnects (to another instance, or this one once restarted)
                    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                        lifetime.ApplicationStopping);

                    // Subscribed before the first (resync) event is sent, so nothing can fall between the two
                    using var subscription = eventHub.Subscribe(userId);

                    await foreach (var item in StreamPacketEvents(subscription.Reader,
                                       packet => JsonSerializer.Serialize(ToResponsePacket(packet, encodingService),
                                           jsonOptions.Value.SerializerOptions),
                                       HeartbeatInterval,
                                       stopping.Token))
                        yield return item;
                }
            });
}
