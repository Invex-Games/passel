namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     What discarding an upload came to.
/// </summary>
[Union]
internal partial record DeleteParcelResult
{
    /// <summary>
    ///     The upload is marked deleted, and the sweep has been woken to erase its bytes.
    /// </summary>
    public sealed partial record Deleted;

    /// <summary>
    ///     The user has no such upload: it never existed, is another user's, is still uploading or is deleted
    ///     already.
    /// </summary>
    public sealed partial record NotFound;

    /// <summary>
    ///     The upload has been sent in a packet; deleting the packet deletes it.
    /// </summary>
    public sealed partial record Attached;
}

internal interface IParcelStoreService
{
    /// <summary>
    ///     Records a new upload as <see cref="ParcelStatus.Uploading" />, reserving <paramref name="reservedBytes" />
    ///     against the user's quota, before any of its bytes are read. Returns the parcel's id.
    /// </summary>
    Task<Guid> BeginUploadAsync(string userId, string? fileName, long reservedBytes, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks the upload <see cref="ParcelStatus.Uploaded" /> with what its bytes turned out to be. Returns
    ///     <c>null</c> when it is no longer uploading: the sweep took it for a stuck upload and deleted it meanwhile.
    /// </summary>
    Task<Parcel?> CompleteUploadAsync(
        Guid id,
        ParcelInspection inspection,
        long size,
        string sha256,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a failed upload deleted, so the sweep erases whatever bytes of it were stored. Runs to the end
    ///     whether or not the request was cancelled, and does not throw for a race it lost.
    /// </summary>
    Task FailUploadAsync(Guid id);

    Task<DeleteParcelResult> Handle(DeleteParcel command, CancellationToken cancellationToken);

    /// <summary>
    ///     The parcel if the user owns it and its upload is complete, sent or not; otherwise <c>null</c>.
    /// </summary>
    Task<Parcel?> LoadReadableAsync(string userId, Guid id, CancellationToken cancellationToken);
}

/// <summary>
///     Writes parcel documents through an upload, a discard and a failure. Every change to an existing parcel is a
///     Marten update with a version check, so of two writers racing on one parcel (an upload completing as the sweep
///     expires it, a discard as it is sent) exactly one wins and the other reads again. The bytes are not touched
///     here: the upload endpoint stores them, and the sweep erases those of deleted parcels.
/// </summary>
internal sealed partial class ParcelStoreService(
    IDocumentSession session,
    TimeProvider time,
    IParcelSweepSignal sweep,
    ILogger<ParcelStoreService> logger) : IParcelStoreService
{
    public async Task<Guid> BeginUploadAsync(
        string userId,
        string? fileName,
        long reservedBytes,
        CancellationToken cancellationToken)
    {
        var parcel = new Parcel
        {
            // Version 7, so ids order parcels by upload
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = time.GetUtcNow(),
            Status = ParcelStatus.Uploading,
            Size = reservedBytes,
            ContentType = ParcelSniffer.OctetStream,
            FileName = fileName,
        };

        session.Insert(parcel);
        await session.SaveChangesAsync(cancellationToken);

        return parcel.Id;
    }

    public async Task<Parcel?> CompleteUploadAsync(
        Guid id,
        ParcelInspection inspection,
        long size,
        string sha256,
        CancellationToken cancellationToken)
    {
        var parcel = await session.LoadAsync<Parcel>(id, cancellationToken);

        if (parcel is not { Status: ParcelStatus.Uploading })
            return null;

        var uploaded = parcel with
        {
            Status = ParcelStatus.Uploaded,
            Size = size,
            ContentType = inspection.ContentType,
            IsImage = inspection.IsImage,
            Width = inspection.Width,
            Height = inspection.Height,
            HasThumbnail = inspection.Thumbnail is not null,
            Sha256 = sha256,
        };

        session.Update(uploaded);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            session.EjectAllPendingChanges();

            return null;
        }

        return uploaded;
    }

    public async Task FailUploadAsync(Guid id)
    {
        // A save that failed for a reason other than a race leaves its changes pending, and would send them again
        session.EjectAllPendingChanges();

        var parcel = await session.LoadAsync<Parcel>(id, CancellationToken.None);

        if (parcel is null or { Status: ParcelStatus.Deleted })
            return;

        session.Update(parcel with
        {
            Status = ParcelStatus.Deleted,
            DeletedAt = time.GetUtcNow(),
        });

        try
        {
            await session.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            // Only the sweep changes an upload in progress, and only to delete it, so the parcel is deleted either way
            session.EjectAllPendingChanges();
            LogFailedUploadSwept(id);

            return;
        }

        sweep.Poke();
    }

    public async Task<DeleteParcelResult> Handle(DeleteParcel command, CancellationToken cancellationToken)
    {
        var parcel = await session.LoadAsync<Parcel>(command.ParcelId, cancellationToken);

        if (Refusal(parcel, command.UserId) is { } refused)
            return refused;

        session.Update(parcel! with
        {
            Status = ParcelStatus.Deleted,
            DeletedAt = time.GetUtcNow(),
        });

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            // Sent, discarded or expired meanwhile. Whatever won settled the parcel, so the answer is what the parcel
            // is now, and one more read is enough
            session.EjectAllPendingChanges();

            var current = await session.LoadAsync<Parcel>(command.ParcelId, cancellationToken);

            return Refusal(current, command.UserId) ?? new DeleteParcelResult.NotFound();
        }

        sweep.Poke();

        return new DeleteParcelResult.Deleted();
    }

    public async Task<Parcel?> LoadReadableAsync(string userId, Guid id, CancellationToken cancellationToken)
    {
        var parcel = await session.LoadAsync<Parcel>(id, cancellationToken);

        // Another user's parcel is reported as missing too, so parcel ids cannot be probed
        return parcel is { Status: ParcelStatus.Uploaded } && parcel.UserId == userId
            ? parcel
            : null;
    }

    /// <summary>
    ///     Why <paramref name="parcel" /> may not be discarded, or <c>null</c> when it is the user's, uploaded and
    ///     unsent, and may be.
    /// </summary>
    private static DeleteParcelResult? Refusal(Parcel? parcel, string userId)
    {
        if (parcel is not { Status: ParcelStatus.Uploaded } || parcel.UserId != userId)
            return new DeleteParcelResult.NotFound();

        if (parcel.AttachedPacketId is not null)
            return new DeleteParcelResult.Attached();

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parcel {ParcelId} was deleted by the sweep before its failed upload could be")]
    private partial void LogFailedUploadSwept(Guid parcelId);
}
