using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Threading.Channels;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.Tests;

public sealed class GetPacketEventsTests
{
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    private static Packet CreatePacket(string body) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            CreatedAt = DateTimeOffset.UtcNow,
            Content = new PacketContent.Text
            {
                Title = null,
                Body = body,
            },
        };

    private static string Serialize(Packet packet) =>
        ((PacketContent.Text)packet.Content).Body;

    private static string SerializeDeleted(Guid packetId) =>
        $"deleted {packetId}";

    private static async Task<List<SseItem<string>>> Collect(
        Channel<PacketEvent> channel,
        TimeSpan heartbeatInterval,
        int count)
    {
        var items = new List<SseItem<string>>();

        await foreach (var item in PacketEndpoints.StreamPacketEvents(channel.Reader,
                           Serialize,
                           SerializeDeleted,
                           heartbeatInterval,
                           null,
                           TestContext.Current.CancellationToken))
        {
            items.Add(item);

            if (items.Count == count)
                break;
        }

        return items;
    }

    private static DefaultHttpContext CreateHttpContext() =>
        new()
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
            Response =
            {
                Body = new MemoryStream(),
            },
        };

    private static async Task<string> ReadBodyAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;

        return await new StreamReader(httpContext.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Starts_with_a_resync_then_streams_events_in_order()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("first")));
        channel.Writer.TryWrite(new PacketEvent.Resync());
        channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("second")));

        var items = await Collect(channel, LongHeartbeat, 4);

        Assert.Equal([
                (PacketEndpoints.ResyncEventType, ""),
                (PacketEndpoints.PacketCreatedEventType, "first"),
                (PacketEndpoints.ResyncEventType, ""),
                (PacketEndpoints.PacketCreatedEventType, "second"),
            ],
            items.Select(i => (i.EventType, i.Data)));
    }

    [Fact]
    public async Task Streams_deleted_packets()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        var packetId = Guid.CreateVersion7();
        channel.Writer.TryWrite(new PacketEvent.Deleted(packetId));

        var items = await Collect(channel, LongHeartbeat, 2);

        Assert.Equal((PacketEndpoints.PacketDeletedEventType, $"deleted {packetId}"),
            (items[1].EventType, items[1].Data));
    }

    [Fact]
    public async Task Streams_updated_packets_in_the_shape_of_created_ones()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        channel.Writer.TryWrite(new PacketEvent.Updated(CreatePacket("with preview")));

        var items = await Collect(channel, LongHeartbeat, 2);

        Assert.Equal((PacketEndpoints.PacketUpdatedEventType, "with preview"), (items[1].EventType, items[1].Data));
    }

    [Fact]
    public async Task Sends_heartbeats_while_idle()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();

        var items = await Collect(channel, TimeSpan.FromMilliseconds(20), 3);

        Assert.Equal([
                PacketEndpoints.ResyncEventType,
                PacketEndpoints.HeartbeatEventType,
                PacketEndpoints.HeartbeatEventType,
            ],
            items.Select(i => i.EventType));
    }

    [Fact]
    public async Task Delivers_an_event_written_after_a_heartbeat()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        var items = new List<SseItem<string>>();

        await foreach (var item in PacketEndpoints.StreamPacketEvents(channel.Reader,
                           Serialize,
                           SerializeDeleted,
                           TimeSpan.FromMilliseconds(20),
                           null,
                           TestContext.Current.CancellationToken))
        {
            items.Add(item);

            if (item.EventType == PacketEndpoints.HeartbeatEventType)
                channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("late")));

            if (item.EventType == PacketEndpoints.PacketCreatedEventType)
                break;
        }

        Assert.Equal("late", items[^1].Data);
    }

    [Fact]
    public async Task Ends_when_the_subscription_completes()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("only")));
        channel.Writer.Complete();

        var items = await Collect(channel, LongHeartbeat, int.MaxValue);

        Assert.Equal([PacketEndpoints.ResyncEventType, PacketEndpoints.PacketCreatedEventType],
            items.Select(i => i.EventType));
    }

    [Fact]
    public async Task Writes_a_data_line_for_events_without_data()
    {
        // Clients only dispatch events that have a data line, so an empty resync must still carry one
        var channel = Channel.CreateUnbounded<PacketEvent>();
        channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("body")));
        channel.Writer.Complete();

        var httpContext = CreateHttpContext();

        await TypedResults
            .ServerSentEvents(PacketEndpoints.StreamPacketEvents(channel.Reader,
                Serialize,
                SerializeDeleted,
                LongHeartbeat,
                null,
                TestContext.Current.CancellationToken))
            .ExecuteAsync(httpContext);

        Assert.Equal("text/event-stream", httpContext.Response.ContentType);
        Assert.Equal("event: resync\ndata: \n\nevent: packet-created\ndata: body\n\n", await ReadBodyAsync(httpContext));
    }

    [Fact]
    public async Task Sends_the_reconnection_interval_with_the_resync()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        channel.Writer.TryWrite(new PacketEvent.Created(CreatePacket("body")));
        channel.Writer.Complete();
        var httpContext = CreateHttpContext();

        await TypedResults
            .ServerSentEvents(PacketEndpoints.StreamPacketEvents(channel.Reader,
                Serialize,
                SerializeDeleted,
                LongHeartbeat,
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken))
            .ExecuteAsync(httpContext);

        // Only the first event carries it; the client keeps the value for the whole connection
        Assert.Equal("event: resync\ndata: \nretry: 3000\n\nevent: packet-created\ndata: body\n\n",
            await ReadBodyAsync(httpContext));
    }

    [Fact]
    public void NextReconnectionInterval_stays_within_its_range()
    {
        var random = new Random(42);

        var intervals = Enumerable
            .Range(0, 1000)
            .Select(_ => PacketEndpoints.NextReconnectionInterval(random))
            .ToList();

        Assert.All(intervals,
            interval => Assert.InRange(interval,
                PacketEndpoints.MinReconnectionInterval,
                PacketEndpoints.MaxReconnectionInterval));
        Assert.True(intervals.Distinct().Count() > 1);
    }

    [Fact]
    public async Task Heartbeats_are_timed_without_throwing()
    {
        // Timing them with Task.WaitAsync threw and caught a TimeoutException per heartbeat per stream. A first-chance
        // handler sees every exception thrown in the process, so none may be a timeout while heartbeats go out
        var channel = Channel.CreateUnbounded<PacketEvent>();
        var timeouts = 0;

        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is TimeoutException)
                Interlocked.Increment(ref timeouts);
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;

        try
        {
            var items = await Collect(channel, TimeSpan.FromMilliseconds(20), 4);

            Assert.Equal(3, items.Count(item => item.EventType == PacketEndpoints.HeartbeatEventType));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        Assert.Equal(0, timeouts);
    }

    [Fact]
    public async Task Ends_without_throwing_when_the_request_is_aborted()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        using var aborted = new CancellationTokenSource();
        var items = new List<SseItem<string>>();

        await foreach (var item in PacketEndpoints.StreamPacketEvents(channel.Reader,
                           Serialize,
                           SerializeDeleted,
                           LongHeartbeat,
                           null,
                           aborted.Token))
        {
            items.Add(item);
            await aborted.CancelAsync();
        }

        Assert.Single(items);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static ClaimsPrincipal UserExpiringAt(DateTimeOffset expiresAt) =>
        UserWithExpiry(expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static ClaimsPrincipal UserWithExpiry(string? exp) =>
        new(new ClaimsIdentity(exp is null ? [] : [new Claim("exp", exp)], "test"));

    [Fact]
    public void StreamLifetime_ends_when_the_token_would_stop_validating()
    {
        var lifetime = PacketEndpoints.StreamLifetime(UserExpiringAt(Now.AddMinutes(30)), Now, ClockSkew);

        Assert.Equal(TimeSpan.FromMinutes(35), lifetime);
    }

    [Fact]
    public void StreamLifetime_is_zero_for_a_token_past_its_skew()
    {
        var lifetime = PacketEndpoints.StreamLifetime(UserExpiringAt(Now.AddMinutes(-10)), Now, ClockSkew);

        Assert.Equal(TimeSpan.Zero, lifetime);
    }

    [Fact]
    public void StreamLifetime_is_capped_for_a_long_lived_token()
    {
        var lifetime = PacketEndpoints.StreamLifetime(UserExpiringAt(Now.AddDays(30)), Now, ClockSkew);

        Assert.Equal(PacketEndpoints.MaxStreamLifetime, lifetime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("-5")]
    public void StreamLifetime_is_the_cap_without_a_usable_expiry(string? exp)
    {
        var lifetime = PacketEndpoints.StreamLifetime(UserWithExpiry(exp), Now, ClockSkew);

        Assert.Equal(PacketEndpoints.MaxStreamLifetime, lifetime);
    }
}
