using Marten.Exceptions;
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

    /// <summary>
    ///     Stores the preview on the user's link and announces the update. Returns <c>false</c>, and writes nothing,
    ///     when the user has no such link any more: deleted since its page was fetched, which is no error.
    /// </summary>
    Task<bool> Handle(SetLinkPreview command);
}

/// <summary>
///     Writes packets as documents: one upsert to create, one delete to remove, one update to attach a link's
///     preview, and a notification after each commit for the live streams. A link's page is queued for fetching
///     once the link is committed.
/// </summary>
internal sealed partial class PacketStoreService(
    IDocumentSession session,
    NpgsqlDataSource dataSource,
    ILinkPreviewQueue previewQueue,
    ILogger<PacketStoreService> logger) : IPacketStoreService
{
    public Task<Packet> Handle(CreateTextPacket command) =>
        StoreAsync(command.UserId,
            new PacketContent.Text
            {
                Title = command.Title,
                Body = command.Body,
            });

    public async Task<Packet> Handle(CreateLinkPacket command)
    {
        var packet = await StoreAsync(command.UserId,
            new PacketContent.Link
            {
                Name = command.Title,
                Url = command.Url,
            });

        // The page is fetched in the background, after the commit, so a slow or dead site never holds up the create
        // and a rolled-back link is never fetched for. The queue says no when previews are off or it is full, and
        // the link then simply has no preview
        previewQueue.TryEnqueue(new(packet.Id, command.UserId, command.Url));

        return packet;
    }

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

    public async Task<bool> Handle(SetLinkPreview command)
    {
        var packet = await session.LoadAsync<Packet>(command.PacketId);

        if (packet is null || packet.UserId != command.UserId || packet.Content is not PacketContent.Link link)
            return false;

        var updated = new Packet
        {
            Id = packet.Id,
            UserId = packet.UserId,
            CreatedAt = packet.CreatedAt,
            Content = link with
            {
                Preview = command.Preview,
            },
        };

        // An update, not a store: a store is an upsert, which would bring back a link deleted between the load
        // above and the save. Marten refuses to update a document that is gone, and the preview goes with it
        session.Update(updated);

        try
        {
            await session.SaveChangesAsync();
        }
        catch (NonExistentDocumentException)
        {
            return false;
        }

        await NotifyAsync(new(command.UserId, command.PacketId, PacketChange.Updated));

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
