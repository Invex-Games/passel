using System.Net.ServerSentEvents;
using System.Threading.Channels;
using Invex.Passel.WebApi.Domain.Aggregates;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.Tests;

public sealed class GetPacketEventsTests
{
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    private static PacketAggregate CreatePacket(string body) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            Packet = new Packet.Text
            {
                Title = null,
                Body = body,
            },
        };

    private static string Serialize(PacketAggregate packet) =>
        ((Packet.Text)packet.Packet).Body;

    private static async Task<List<SseItem<string>>> Collect(
        Channel<PacketEvent> channel,
        TimeSpan heartbeatInterval,
        int count)
    {
        var items = new List<SseItem<string>>();

        await foreach (var item in PacketEndpoints.StreamPacketEvents(channel.Reader,
                           Serialize,
                           heartbeatInterval,
                           TestContext.Current.CancellationToken))
        {
            items.Add(item);

            if (items.Count == count)
                break;
        }

        return items;
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
                           TimeSpan.FromMilliseconds(20),
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

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
            Response =
            {
                Body = new MemoryStream(),
            },
        };

        await TypedResults
            .ServerSentEvents(PacketEndpoints.StreamPacketEvents(channel.Reader,
                Serialize,
                LongHeartbeat,
                TestContext.Current.CancellationToken))
            .ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", httpContext.Response.ContentType);
        Assert.Equal("event: resync\ndata: \n\nevent: packet-created\ndata: body\n\n", body);
    }

    [Fact]
    public async Task Ends_without_throwing_when_the_request_is_aborted()
    {
        var channel = Channel.CreateUnbounded<PacketEvent>();
        using var aborted = new CancellationTokenSource();
        var items = new List<SseItem<string>>();

        await foreach (var item in PacketEndpoints.StreamPacketEvents(channel.Reader,
                           Serialize,
                           LongHeartbeat,
                           aborted.Token))
        {
            items.Add(item);
            await aborted.CancelAsync();
        }

        Assert.Single(items);
    }
}
