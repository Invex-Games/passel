using System.Threading.Channels;
using Npgsql;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Listens for <see cref="PacketNotification" />s on a dedicated Postgres connection and publishes the created
///     packets to the <see cref="IPacketEventHub" />. Every API instance listens, so a packet created through any
///     instance reaches the streams open on all of them.
/// </summary>
internal sealed partial class PacketNotificationListener(
    NpgsqlDataSource dataSource,
    IDocumentStore store,
    IPacketEventHub hub,
    ILogger<PacketNotificationListener> logger) : BackgroundService
{
    /// <summary>
    ///     Loads the snapshots of the given packets; <c>null</c> when they could not be loaded.
    /// </summary>
    internal delegate Task<IReadOnlyList<PacketAggregate>?> PacketLoader(
        IReadOnlyList<Guid> packetIds,
        CancellationToken cancellationToken);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long the connection may sit idle before it is probed, so a silently dropped connection is noticed.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     The window over which the resync after a reconnect reaches the open streams, so the re-fetches it triggers
    ///     do not all hit a database that may just have recovered at the same moment.
    /// </summary>
    private static readonly TimeSpan ResyncSpread = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Npgsql raises one notification per wait on the connection, so the connection is read in a loop of its own
        // that only queues, and a second loop dispatches: everything that arrives while a batch is being dispatched
        // becomes the next batch. Unbounded, because a batch costs one query however large it is, so the queue only
        // grows while the database is failing, and a failed batch is cheap
        var queue = Channel.CreateUnbounded<PacketNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var dispatching = DispatchQueueAsync(queue.Reader, DispatchBatchAsync, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ListenAsync(queue.Writer, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogListenFailed(exception);
                }

                try
                {
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            await dispatching;
        }
    }

    private async Task ListenAsync(ChannelWriter<PacketNotification> queue, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Raised synchronously while Npgsql reads from the connection, which is why the handler only queues
        connection.Notification += (_, args) =>
        {
            if (PacketNotification.TryParse(args.Payload, out var notification))
                queue.TryWrite(notification);
        };

        await using (var listen = new NpgsqlCommand($"LISTEN {PacketNotification.Channel}", connection))
            await listen.ExecuteNonQueryAsync(cancellationToken);

        // Notifications sent while this instance wasn't listening are lost, so streams that stayed open re-fetch
        hub.PublishToAll(new PacketEvent.Resync(), ResyncSpread);

        while (true)
        {
            // Returns once a notification has been raised; at once, while more are already buffered
            if (!await connection.WaitAsync(KeepAliveInterval, cancellationToken))
            {
                await using var ping = new NpgsqlCommand("SELECT 1", connection);
                await ping.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Hands the queued notifications to <paramref name="dispatchBatch" /> in batches: everything queued when
    ///     the loop wakes up goes in one call. Ends when the queue completes or the token is cancelled.
    /// </summary>
    internal static async Task DispatchQueueAsync(
        ChannelReader<PacketNotification> queue,
        Func<IReadOnlyList<PacketNotification>, CancellationToken, Task> dispatchBatch,
        CancellationToken cancellationToken)
    {
        var batch = new List<PacketNotification>();

        try
        {
            while (await queue.WaitToReadAsync(cancellationToken))
            {
                while (queue.TryRead(out var notification))
                    batch.Add(notification);

                await dispatchBatch(batch, cancellationToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchBatchAsync(IReadOnlyList<PacketNotification> batch, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(batch, hub, LoadPacketsAsync, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing in a dispatch is expected to throw, but the loop has to outlive a surprise, and the users in
            // the batch can recover by re-fetching
            LogDispatchFailed(exception, batch.Count);

            foreach (var userId in batch.Select(notification => notification.UserId).Distinct())
                hub.Publish(userId, new PacketEvent.Resync());
        }
    }

    /// <summary>
    ///     Publishes one batch of notifications: the created packets of users with a stream open on this instance are
    ///     loaded with a single query, deletes need no load, and the events go out in the order the notifications
    ///     arrived. When the load fails, each affected user gets one resync in place of their created packets.
    /// </summary>
    internal static async Task DispatchAsync(
        IReadOnlyList<PacketNotification> batch,
        IPacketEventHub hub,
        PacketLoader loadPackets,
        CancellationToken cancellationToken)
    {
        var wanted = new List<PacketNotification>(batch.Count);
        var idsToLoad = new List<Guid>();

        foreach (var notification in batch)
        {
            if (!hub.HasSubscribers(notification.UserId))
                continue;

            wanted.Add(notification);

            if (notification.Change == PacketChange.Created)
                idsToLoad.Add(notification.PacketId);
        }

        if (wanted.Count == 0)
            return;

        var loaded = idsToLoad.Count == 0
            ? []
            : await loadPackets(idsToLoad, cancellationToken);

        Dictionary<Guid, PacketAggregate>? packets = null;

        if (loaded is not null)
        {
            packets = new(loaded.Count);

            foreach (var packet in loaded)
                packets[packet.Id] = packet;
        }

        HashSet<string>? resynced = null;

        foreach (var notification in wanted)
        {
            if (notification.Change == PacketChange.Deleted)
            {
                // The packet no longer exists to load; its id is all clients need
                hub.Publish(notification.UserId, new PacketEvent.Deleted(notification.PacketId));
            }
            else if (packets is null)
            {
                if ((resynced ??= []).Add(notification.UserId))
                    hub.Publish(notification.UserId, new PacketEvent.Resync());
            }
            else if (packets.TryGetValue(notification.PacketId, out var packet) &&
                     packet.UserId == notification.UserId)
            {
                // A packet deleted since it was announced is simply gone, and a notification that names another
                // user's packet is ignored
                hub.Publish(notification.UserId, new PacketEvent.Created(packet));
            }
        }
    }

    /// <summary>
    ///     Loads the snapshots in one query; <c>null</c> when it fails, which is logged.
    /// </summary>
    private async Task<IReadOnlyList<PacketAggregate>?> LoadPacketsAsync(
        IReadOnlyList<Guid> packetIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var session = store.QuerySession();

            return await session.LoadManyAsync<PacketAggregate>(cancellationToken, packetIds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLoadFailed(exception, packetIds.Count);

            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listening for packet notifications failed; reconnecting")]
    private partial void LogListenFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to load {Count} notified packets; their users are told to resync")]
    private partial void LogLoadFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Dispatching a batch of {Count} packet notifications failed; their users are told to resync")]
    private partial void LogDispatchFailed(Exception exception, int count);
}
