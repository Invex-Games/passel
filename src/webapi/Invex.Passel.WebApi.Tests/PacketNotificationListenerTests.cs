using System.Threading.Channels;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class PacketNotificationListenerTests
{
    private static PacketEventHub CreateHub() =>
        new(Microsoft.Extensions.Options.Options.Create(new EventStreamOptions()));

    private static PacketEventSubscription Subscribe(PacketEventHub hub, string userId)
    {
        var subscription = hub.Subscribe(userId);
        Assert.NotNull(subscription);

        return subscription;
    }

    private static Packet CreatePacket(string userId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Content = new PacketContent.Text
            {
                Title = null,
                Body = "body",
            },
        };

    private static List<PacketEvent> Drain(PacketEventSubscription subscription)
    {
        var events = new List<PacketEvent>();

        while (subscription.Reader.TryRead(out var packetEvent))
            events.Add(packetEvent);

        return events;
    }

    /// <summary>
    ///     Stands in for the query: records every call and answers with the packets it knows, or fails.
    /// </summary>
    private sealed class FakeLoader(params IEnumerable<Packet> packets)
    {
        private readonly Dictionary<Guid, Packet> _packets = packets.ToDictionary(p => p.Id);

        public List<IReadOnlyList<Guid>> Calls { get; } = [];

        public bool Fails { get; init; }

        public Task<IReadOnlyList<Packet>?> Load(IReadOnlyList<Guid> packetIds, CancellationToken cancellationToken)
        {
            Calls.Add([..packetIds]);

            IReadOnlyList<Packet>? loaded = Fails
                ? null
                : packetIds
                    .Where(_packets.ContainsKey)
                    .Select(id => _packets[id])
                    .ToList();

            return Task.FromResult(loaded);
        }
    }

    [Fact]
    public async Task Loads_a_batch_with_one_query_and_publishes_each_user_their_events_in_arrival_order()
    {
        var hub = CreateHub();
        using var alice = Subscribe(hub, "alice");
        using var bob = Subscribe(hub, "bob");
        var first = CreatePacket("alice");
        var second = CreatePacket("bob");
        var third = CreatePacket("alice");
        var deletedId = Guid.CreateVersion7();
        var loader = new FakeLoader(first, second, third);

        await PacketNotificationListener.DispatchAsync([
                new("alice", first.Id),
                new("bob", second.Id),
                new("alice", deletedId, PacketChange.Deleted),
                new("alice", third.Id),
            ],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        var call = Assert.Single(loader.Calls);
        Assert.Equal([first.Id, second.Id, third.Id], call);
        Assert.Equal([new PacketEvent.Created(first), new PacketEvent.Deleted(deletedId), new PacketEvent.Created(third)],
            Drain(alice));
        Assert.Equal([new PacketEvent.Created(second)], Drain(bob));
    }

    [Fact]
    public async Task Only_users_with_a_stream_open_here_are_loaded_for()
    {
        var hub = CreateHub();
        using var alice = Subscribe(hub, "alice");
        var alices = CreatePacket("alice");
        var carols = CreatePacket("carol");
        var loader = new FakeLoader(alices, carols);

        await PacketNotificationListener.DispatchAsync([new("carol", carols.Id), new("alice", alices.Id)],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        Assert.Equal([alices.Id], Assert.Single(loader.Calls));
        Assert.Equal([new PacketEvent.Created(alices)], Drain(alice));
    }

    [Fact]
    public async Task Nothing_is_loaded_when_no_one_is_listening()
    {
        var hub = CreateHub();
        var loader = new FakeLoader();

        await PacketNotificationListener.DispatchAsync([new("carol", Guid.CreateVersion7())],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        Assert.Empty(loader.Calls);
    }

    [Fact]
    public async Task Deletes_are_published_without_a_load()
    {
        var hub = CreateHub();
        using var alice = Subscribe(hub, "alice");
        var deletedId = Guid.CreateVersion7();
        var loader = new FakeLoader();

        await PacketNotificationListener.DispatchAsync([new("alice", deletedId, PacketChange.Deleted)],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        Assert.Empty(loader.Calls);
        Assert.Equal([new PacketEvent.Deleted(deletedId)], Drain(alice));
    }

    [Fact]
    public async Task A_failed_load_tells_each_user_to_resync_once_and_still_publishes_deletes()
    {
        var hub = CreateHub();
        using var alice = Subscribe(hub, "alice");
        using var bob = Subscribe(hub, "bob");
        var deletedId = Guid.CreateVersion7();
        var loader = new FakeLoader { Fails = true };

        await PacketNotificationListener.DispatchAsync([
                new("alice", Guid.CreateVersion7()),
                new("alice", Guid.CreateVersion7()),
                new("alice", deletedId, PacketChange.Deleted),
                new("bob", Guid.CreateVersion7()),
            ],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        Assert.Single(loader.Calls);
        Assert.Equal([new PacketEvent.Resync(), new PacketEvent.Deleted(deletedId)], Drain(alice));
        Assert.Equal([new PacketEvent.Resync()], Drain(bob));
    }

    [Fact]
    public async Task Packets_that_are_gone_or_belong_to_someone_else_are_not_published()
    {
        var hub = CreateHub();
        using var alice = Subscribe(hub, "alice");
        using var bob = Subscribe(hub, "bob");
        var alices = CreatePacket("alice");
        var loader = new FakeLoader(alices);

        // Bob's notification names Alice's packet; Alice's names one that was deleted before it could be loaded
        await PacketNotificationListener.DispatchAsync([new("bob", alices.Id), new("alice", Guid.CreateVersion7())],
            hub,
            loader.Load,
            TestContext.Current.CancellationToken);

        Assert.Empty(Drain(alice));
        Assert.Empty(Drain(bob));
    }

    [Fact]
    public async Task The_queue_is_dispatched_in_batches_of_whatever_arrived_meanwhile()
    {
        var queue = Channel.CreateUnbounded<PacketNotification>();
        var batches = new List<List<PacketNotification>>();
        var arrivedDuringFirstBatch = new PacketNotification("bob", Guid.CreateVersion7());
        var arrivedDuringFirstBatchToo = new PacketNotification("carol", Guid.CreateVersion7());
        var queuedBeforeStart = new[] { new PacketNotification("alice", Guid.CreateVersion7()), new PacketNotification("alice", Guid.CreateVersion7()) };

        foreach (var notification in queuedBeforeStart)
            queue.Writer.TryWrite(notification);

        await PacketNotificationListener.DispatchQueueAsync(queue.Reader,
            (batch, _) =>
            {
                batches.Add([..batch]);

                // Arrivals while a batch is being dispatched form the next one; the queue then completes
                if (batches.Count == 1)
                {
                    queue.Writer.TryWrite(arrivedDuringFirstBatch);
                    queue.Writer.TryWrite(arrivedDuringFirstBatchToo);
                }
                else
                {
                    queue.Writer.Complete();
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, batches.Count);
        Assert.Equal(queuedBeforeStart, batches[0]);
        Assert.Equal([arrivedDuringFirstBatch, arrivedDuringFirstBatchToo], batches[1]);
    }

    [Fact]
    public async Task The_queue_loop_ends_quietly_when_cancelled()
    {
        var queue = Channel.CreateUnbounded<PacketNotification>();
        using var cancellation = new CancellationTokenSource();
        var dispatching = PacketNotificationListener.DispatchQueueAsync(queue.Reader,
            (_, _) => Task.CompletedTask,
            cancellation.Token);

        await cancellation.CancelAsync();

        await dispatching;
    }
}
