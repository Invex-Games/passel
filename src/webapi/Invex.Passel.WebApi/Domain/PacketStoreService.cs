using Npgsql;

namespace Invex.Passel.WebApi.Domain;

internal interface IPacketStoreService
{
    Task<Packet> Handle(CreateTextPacket command);

    Task<Packet> Handle(CreateLinkPacket command);

    /// <summary>
    ///     Returns <c>false</c> if the user has no such packet.
    /// </summary>
    Task<bool> Handle(DeletePacket command);
}

/// <summary>
///     Writes packets as documents: one upsert to create, one delete to remove, and a notification after each commit
///     for the live streams.
/// </summary>
internal sealed partial class PacketStoreService(
    IDocumentSession session,
    NpgsqlDataSource dataSource,
    ILogger<PacketStoreService> logger) : IPacketStoreService
{
    public Task<Packet> Handle(CreateTextPacket command) =>
        StoreAsync(command.UserId,
            new PacketContent.Text
            {
                Title = command.Title,
                Body = command.Body,
            });

    public Task<Packet> Handle(CreateLinkPacket command) =>
        StoreAsync(command.UserId,
            new PacketContent.Link
            {
                Name = command.Title,
                Url = command.Url,
            });

    public async Task<bool> Handle(DeletePacket command)
    {
        var packet = await session.LoadAsync<Packet>(command.PacketId);

        // Another user's packet is reported as missing too, so packet ids can't be probed
        if (packet is null || packet.UserId != command.UserId)
            return false;

        // The document is the only record of the packet, so this is the end of its content
        session.Delete(packet);

        await session.SaveChangesAsync();
        await NotifyAsync(new(command.UserId, command.PacketId, PacketChange.Deleted));

        return true;
    }

    private async Task<Packet> StoreAsync(string userId, PacketContent content)
    {
        var packet = new Packet
        {
            // Version 7, so ids order packets by creation; the feed pages on them
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Content = content,
        };

        session.Store(packet);

        await session.SaveChangesAsync();
        await NotifyAsync(new(userId, packet.Id));

        return packet;
    }

    // Sent once the change is committed, so it never announces one that was rolled back. It can't be queued in the
    // session: Marten reads its batch's results positionally and would hand the pg_notify result to the next
    // operation. A lost notification only delays live clients until their next resync, so it doesn't fail the request.
    private async Task NotifyAsync(PacketNotification notification)
    {
        try
        {
            await using var notify = dataSource.CreateCommand("select pg_notify($1, $2)");
            notify.Parameters.Add(new() { Value = PacketNotification.Channel });
            notify.Parameters.Add(new() { Value = notification.ToPayload() });
            await notify.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            LogNotifyFailed(exception, notification.PacketId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to notify listeners of packet {PacketId}")]
    private partial void LogNotifyFailed(Exception exception, Guid packetId);
}
