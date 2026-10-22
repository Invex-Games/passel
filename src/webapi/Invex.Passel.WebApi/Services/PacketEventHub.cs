using System.Threading.Channels;
using Microsoft.Extensions.Options;

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
    /// <summary>
    ///     Opens a stream for the user. When they already have as many as allowed, their oldest is closed so its
    ///     client reconnects. Returns <c>null</c> when this instance has no room for another stream.
    /// </summary>
    PacketEventSubscription? Subscribe(string userId);

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
internal sealed class PacketEventHub(IOptions<EventStreamOptions> options) : IPacketEventHub
{
    internal const int SubscriberBufferSize = 64;

    private readonly int _maxStreamsPerUser = options.Value.MaxStreamsPerUser;
    private readonly int _maxStreamsPerInstance = options.Value.MaxStreamsPerInstance;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Channel<PacketEvent>>> _subscribers = [];
    private int _count;

    public PacketEventSubscription? Subscribe(string userId)
    {
        var channel = Channel.CreateBounded<PacketEvent>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            SingleReader = true,
        });

        Channel<PacketEvent>? evicted = null;

        lock (_lock)
        {
            _subscribers.TryGetValue(userId, out var channels);

            if (channels is not null && channels.Count >= _maxStreamsPerUser)
            {
                // The user replaces a stream rather than adding one, so the instance cap does not apply
                evicted = channels[0];
                channels.RemoveAt(0);
                _count--;
            }
            else if (_count >= _maxStreamsPerInstance)
            {
                return null;
            }

            if (channels is null)
                _subscribers[userId] = channels = [];

            channels.Add(channel);
            _count++;
        }

        // Completing the channel ends its stream and the client reconnects. It is already out of the list, so
        // disposing that stream is a no-op and cannot cascade into evicting another one
        evicted?.Writer.TryComplete();

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
            if (!_subscribers.TryGetValue(userId, out var channels) || !channels.Remove(channel))
                return;

            _count--;

            if (channels.Count == 0)
                _subscribers.Remove(userId);
        }
    }
}
