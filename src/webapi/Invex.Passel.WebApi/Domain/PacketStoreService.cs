using Npgsql;

namespace Invex.Passel.WebApi.Domain;

internal interface IPacketStoreService
{
    Task<PacketAggregate?> Handle(CreateTextPacket command);

    Task<PacketAggregate?> Handle(CreateLinkPacket command);

    /// <summary>
    ///     Returns <c>false</c> if the user has no such packet.
    /// </summary>
    Task<bool> Handle(DeletePacket command);
}

internal sealed partial class PacketStoreService(
    IDocumentSession session,
    NpgsqlDataSource dataSource,
    ILogger<PacketStoreService> logger) : IPacketStoreService
{
    public async Task<PacketAggregate?> Handle(CreateTextPacket command)
    {
        var id = Guid.CreateVersion7();
        var stream = await session.Events.FetchForWriting<PacketAggregate>(id);

        stream.AppendOne(new TextPacketCreated
        {
            UserId = command.UserId,
            Title = command.Title,
            Body = command.Body,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await session.SaveChangesAsync();
        await NotifyAsync(new(command.UserId, id));

        return await session.Events.FetchLatest<PacketAggregate>(id);
    }

    public async Task<PacketAggregate?> Handle(CreateLinkPacket command)
    {
        var id = Guid.CreateVersion7();
        var stream = await session.Events.FetchForWriting<PacketAggregate>(id);

        stream.AppendOne(new LinkPacketCreated
        {
            UserId = command.UserId,
            Title = command.Title,
            Url = command.Url,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await session.SaveChangesAsync();
        await NotifyAsync(new(command.UserId, id));

        return await session.Events.FetchLatest<PacketAggregate>(id);
    }

    public async Task<bool> Handle(DeletePacket command)
    {
        var packet = await session.LoadAsync<PacketAggregate>(command.PacketId);

        // Another user's packet is reported as missing too, so packet ids can't be probed
        if (packet is null || packet.UserId != command.UserId)
            return false;

        // The inline snapshot projection removes the packet's document when this is applied
        session.Events.Append(command.PacketId,
            new PacketDeleted
            {
                DeletedAt = DateTimeOffset.UtcNow,
            });

        await session.SaveChangesAsync();
        await NotifyAsync(new(command.UserId, command.PacketId, PacketChange.Deleted));

        return true;
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
