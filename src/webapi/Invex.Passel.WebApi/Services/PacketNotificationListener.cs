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
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long the connection may sit idle before it is probed, so a silently dropped connection is noticed.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
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

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Raised synchronously while Npgsql reads from the connection; dispatching needs to query, so it happens
        // after the read returns
        var pending = new List<PacketNotification>();

        connection.Notification += (_, args) =>
        {
            if (PacketNotification.TryParse(args.Payload, out var notification))
                pending.Add(notification);
        };

        await using (var listen = new NpgsqlCommand($"LISTEN {PacketNotification.Channel}", connection))
            await listen.ExecuteNonQueryAsync(cancellationToken);

        // Notifications sent while this instance wasn't listening are lost, so streams that stayed open re-fetch
        hub.PublishToAll(new PacketEvent.Resync());

        while (true)
        {
            if (!await connection.WaitAsync(KeepAliveInterval, cancellationToken))
            {
                await using var ping = new NpgsqlCommand("SELECT 1", connection);
                await ping.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var notification in pending)
                await DispatchAsync(notification, cancellationToken);

            pending.Clear();
        }
    }

    private async Task DispatchAsync(PacketNotification notification, CancellationToken cancellationToken)
    {
        if (!hub.HasSubscribers(notification.UserId))
            return;

        try
        {
            await using var session = store.QuerySession();
            var packet = await session.LoadAsync<PacketAggregate>(notification.PacketId, cancellationToken);

            if (packet is null || packet.UserId != notification.UserId)
                return;

            hub.Publish(notification.UserId, new PacketEvent.Created(packet));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDispatchFailed(exception, notification.PacketId);
            hub.Publish(notification.UserId, new PacketEvent.Resync());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listening for packet notifications failed; reconnecting")]
    private partial void LogListenFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load packet {PacketId} for its notification")]
    private partial void LogDispatchFailed(Exception exception, Guid packetId);
}
