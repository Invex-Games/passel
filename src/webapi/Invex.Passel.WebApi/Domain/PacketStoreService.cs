using Marten.Exceptions;
using Npgsql;

namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     What sending an uploaded file in a packet came to.
/// </summary>
[Union]
internal partial record CreateFilePacketResult
{
    /// <summary>
    ///     The packet is stored and the file attached to it, in one commit.
    /// </summary>
    public sealed partial record Created(Packet Packet);

    /// <summary>
    ///     The user has no such file to send: it never existed, is another user's, is still uploading, or has been
    ///     discarded or expired.
    /// </summary>
    public sealed partial record ParcelNotFound;

    /// <summary>
    ///     The file is in another packet already; a file is sent once.
    /// </summary>
    public sealed partial record AlreadyAttached;
}

internal interface IPacketStoreService
{
    Task<Packet> Handle(CreateTextPacket command);

    Task<Packet> Handle(CreateLinkPacket command);

    /// <summary>
    ///     Stores a File packet for an uploaded file of the user and attaches the file to it, in one commit.
    /// </summary>
    Task<CreateFilePacketResult> Handle(CreateFilePacket command);

    /// <summary>
    ///     Returns <c>false</c> if the user has no such packet. A File packet takes its file with it: the file is
    ///     marked deleted in the same commit, and the sweep is woken to erase its bytes.
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
///     once the link is committed. A File packet and its parcel change together: creating the packet attaches the
///     parcel and deleting it marks the parcel deleted, each in the packet's own commit, with the parcel's version
///     checked so that a racing writer on the parcel (another send, a discard, the sweep) makes this one lose rather
///     than be overwritten. No bytes are touched here; the sweep erases them.
/// </summary>
internal sealed partial class PacketStoreService(
    IDocumentSession session,
    NpgsqlDataSource dataSource,
    ILinkPreviewQueue previewQueue,
    TimeProvider time,
    IParcelSweepSignal sweep,
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

    public async Task<CreateFilePacketResult> Handle(CreateFilePacket command)
    {
        var parcel = await session.LoadAsync<Parcel>(command.ParcelId);

        if (Refusal(parcel, command.UserId) is { } refused)
            return refused;

        var packet = NewPacket(command.UserId,
            new PacketContent.File
            {
                FileName = command.FileName ?? parcel!.FileName,
                ContentType = parcel!.ContentType,
                ParcelId = parcel.Id,
                IsImage = parcel.IsImage,
            });

        // One commit: the packet never exists without its file attached, and the file is never attached to a packet
        // that was rolled back. The update checks the version read above, so of two sends of one file (or a send and
        // a discard, or a send and the sweep's expiry) exactly one commits. The check alone decides this: both sends
        // write the same row, so the unique index on the attached packet (which keeps a packet to one parcel) plays
        // no part
        session.Store(packet);
        session.Update(parcel with
        {
            AttachedPacketId = packet.Id,
        });

        try
        {
            await session.SaveChangesAsync();
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            // Whatever won settled the file, so the answer is what the file is now, and one more read is enough
            session.EjectAllPendingChanges();

            var current = await session.LoadAsync<Parcel>(command.ParcelId);

            return Refusal(current, command.UserId) ?? new CreateFilePacketResult.ParcelNotFound();
        }

        await NotifyAsync(new(command.UserId, packet.Id));

        return new CreateFilePacketResult.Created(packet);
    }

    public async Task<bool> Handle(DeletePacket command)
    {
        var packet = await session.LoadAsync<Packet>(command.PacketId);

        // Another user's packet is reported as missing too, so packet ids can't be probed
        if (packet is null || packet.UserId != command.UserId)
            return false;

        var marksParcel = false;

        // A sent file goes with its packet, in the same commit: it is unavailable the moment the packet is gone
        if (packet.Content is PacketContent.File file &&
            await session.LoadAsync<Parcel>(file.ParcelId) is { } parcel &&
            parcel.UserId == command.UserId &&
            parcel.Status != ParcelStatus.Deleted)
        {
            session.Update(parcel with
            {
                Status = ParcelStatus.Deleted,
                DeletedAt = time.GetUtcNow(),
            });

            marksParcel = true;
        }

        // The document is the only record of the packet, so this is the end of its content
        session.Delete(packet);

        try
        {
            await session.SaveChangesAsync();
        }
        catch (Exception exception) when (marksParcel && MartenRaces.IsLostRace(exception))
        {
            // The file changed since it was read, and only a deletion changes a sent file (the orphan scan, or a
            // delete of this packet racing this one), so the packet goes alone. The orphan scan catches any file this
            // leaves behind
            session.EjectAllPendingChanges();
            LogParcelChangedOnDelete(command.PacketId);

            session.Delete(packet);
            await session.SaveChangesAsync();
        }

        await NotifyAsync(new(command.UserId, command.PacketId, PacketChange.Deleted));

        // The bytes are erased after the commit, by the sweep, never by this request
        if (packet.Content is PacketContent.File)
            sweep.Poke();

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

    private Packet NewPacket(string userId, PacketContent content) =>
        new()
        {
            // Version 7, so ids order packets by creation; the feed pages on them
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = time.GetUtcNow(),
            Content = content,
        };

    /// <summary>
    ///     Why <paramref name="parcel" /> may not be sent, or <c>null</c> when it is the user's, uploaded and unsent,
    ///     and may be. A file that is not the user's is reported as missing, so file ids cannot be probed.
    /// </summary>
    private static CreateFilePacketResult? Refusal(Parcel? parcel, string userId)
    {
        if (parcel is not { Status: ParcelStatus.Uploaded } || parcel.UserId != userId)
            return new CreateFilePacketResult.ParcelNotFound();

        if (parcel.AttachedPacketId is not null)
            return new CreateFilePacketResult.AlreadyAttached();

        return null;
    }

    private async Task<Packet> StoreAsync(string userId, PacketContent content)
    {
        var packet = NewPacket(userId, content);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "The file of packet {PacketId} changed while the packet was deleted; the packet was deleted alone")]
    private partial void LogParcelChangedOnDelete(Guid packetId);
}
