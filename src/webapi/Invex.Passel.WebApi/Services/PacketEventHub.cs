using System.Threading.Channels;

namespace Invex.Passel.WebApi.Services;

[Union]
internal partial record PacketEvent
{
    partial record Created(PacketAggregate Packet);

    partial record Deleted(Guid PacketId);

    /// <summary>
    ///     Packets may have been missed (e.g. the database listener reconnected), so the client should re-fetch.
    /// </summary>
    partial record Resync();
}

internal interface IPacketEventHub
{
    PacketEventSubscription Subscribe(string userId);

    bool HasSubscribers(string userId);

    void Publish(string userId, PacketEvent packetEvent);

    void PublishToAll(PacketEvent packetEvent);
}

internal sealed class PacketEventSubscription(ChannelReader<PacketEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<PacketEvent> Reader { get; } = reader;

    public void Dispose() =>
        unsubscribe();
}

/// <summary>
///     Fans packet events out to the open event streams of each user on this instance.
/// </summary>
internal sealed class PacketEventHub : IPacketEventHub
{
    internal const int SubscriberBufferSize = 64;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Channel<PacketEvent>>> _subscribers = [];

    public PacketEventSubscription Subscribe(string userId)
    {
        var channel = Channel.CreateBounded<PacketEvent>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            SingleReader = true,
        });

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(userId, out var channels))
                _subscribers[userId] = channels = [];

            channels.Add(channel);
        }

        return new(channel.Reader, () => Unsubscribe(userId, channel));
    }

    public bool HasSubscribers(string userId)
    {
        lock (_lock)
            return _subscribers.ContainsKey(userId);
    }

    public void Publish(string userId, PacketEvent packetEvent)
    {
        Channel<PacketEvent>[] channels;

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(userId, out var userChannels))
                return;

            channels = [..userChannels];
        }

        Write(channels, packetEvent);
    }

    public void PublishToAll(PacketEvent packetEvent)
    {
        Channel<PacketEvent>[] channels;

        lock (_lock)
            channels = [.._subscribers.Values.SelectMany(userChannels => userChannels)];

        Write(channels, packetEvent);
    }

    private static void Write(IEnumerable<Channel<PacketEvent>> channels, PacketEvent packetEvent)
    {
        foreach (var channel in channels)
        {
            // A subscriber this far behind has stalled; ending its stream makes the client reconnect and re-fetch
            // rather than silently dropping events
            if (!channel.Writer.TryWrite(packetEvent))
                channel.Writer.TryComplete();
        }
    }

    private void Unsubscribe(string userId, Channel<PacketEvent> channel)
    {
        channel.Writer.TryComplete();

        lock (_lock)
        {
            if (!_subscribers.TryGetValue(userId, out var channels))
                return;

            channels.Remove(channel);

            if (channels.Count == 0)
                _subscribers.Remove(userId);
        }
    }
}
