using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What one sweep did: the parcels it marked deleted because they expired, the deleted ones whose bytes and
///     document it erased, and the deleted ones it could not erase this time because the storage failed.
/// </summary>
internal sealed record ParcelSweepResult(int Expired, int Erased, int Failed);

/// <summary>
///     What one orphan scan did: the sent parcels whose packet was gone and which it marked deleted, and the blobs
///     with no parcel at all which it deleted.
/// </summary>
internal sealed record ParcelOrphanResult(int DanglingReleased, int BlobsDeleted);

internal interface IParcelSweep
{
    /// <summary>
    ///     Marks deleted the parcels that are past their time as of <paramref name="now" /> (unsent for longer than
    ///     <see cref="ParcelOptions.UnattachedLifetime" />, or uploading for longer than
    ///     <see cref="ParcelOptions.UploadingGrace" />), then erases the bytes and document of every deleted parcel.
    /// </summary>
    Task<ParcelSweepResult> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Looks for what the normal path missed: sent parcels whose packet is gone, which it marks deleted for the
    ///     next sweep to erase, and blobs older than <see cref="ParcelOptions.OrphanBlobMinimumAge" /> as of
    ///     <paramref name="now" /> that belong to no parcel, which it deletes.
    /// </summary>
    Task<ParcelOrphanResult> ScanOrphansAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
///     Expires and erases parcels. Every change it makes to a parcel is a Marten update with a version check, made
///     from the parcel as it was read, so a parcel sent, completed or discarded after the sweep found it makes the
///     sweep's write lose rather than undo it; the sweep then leaves the parcel alone. Bytes are deleted before the
///     document, so a crash part way only repeats a delete, which is idempotent. It announces nothing: a parcel is
///     not a packet, and a deleted packet was announced when it was deleted.
/// </summary>
internal sealed partial class ParcelSweep(
    IDocumentSession session,
    NpgsqlDataSource dataSource,
    IDocumentStore store,
    IParcelStore parcelStore,
    IOptions<ParcelOptions> options,
    ILogger<ParcelSweep> logger) : IParcelSweep
{
    /// <summary>
    ///     The most batches one sweep expires or erases, so a backlog is worked off over several sweeps rather than
    ///     in one that never ends.
    /// </summary>
    private const int MaxBatches = 10;

    /// <summary>
    ///     The most sent parcels with no packet that one orphan scan releases.
    /// </summary>
    private const int MaxDangling = 500;

    /// <summary>
    ///     How many blobs the orphan scan checks against the parcels at once.
    /// </summary>
    private const int OrphanCheckBatchSize = 100;

    private readonly ParcelOptions _options = options.Value;

    // The status is stored as its number and an unsent parcel's packet as JSON null (see ParcelQuotaService)
    private readonly string _danglingSql = $"""
        select p.id
        from {store.Options.FindOrResolveDocumentType(typeof(Parcel)).TableName.QualifiedName} as p
        left join {store.Options.FindOrResolveDocumentType(typeof(Packet)).TableName.QualifiedName} as k
            on k.id = (p.data ->> '{nameof(Parcel.AttachedPacketId)}')::uuid
        where (p.data ->> '{nameof(Parcel.Status)}')::int = {(int)ParcelStatus.Uploaded}
            and p.data ->> '{nameof(Parcel.AttachedPacketId)}' is not null
            and k.id is null
        limit {MaxDangling}
        """;

    /// <summary>
    ///     The parcels that are past their time as of <paramref name="now" />: uploaded and unsent for longer than
    ///     the unattached lifetime, or still uploading after the grace an upload has to finish.
    /// </summary>
    internal static Expression<Func<Parcel, bool>> Expirable(DateTimeOffset now, ParcelOptions options)
    {
        var unsentBefore = now - options.UnattachedLifetime;
        var uploadingBefore = now - options.UploadingGrace;

        return parcel =>
            (parcel.Status == ParcelStatus.Uploaded && parcel.AttachedPacketId == null && parcel.CreatedAt < unsentBefore) ||
            (parcel.Status == ParcelStatus.Uploading && parcel.CreatedAt < uploadingBefore);
    }

    /// <summary>
    ///     The parcels whose bytes and document are to be erased.
    /// </summary>
    internal static Expression<Func<Parcel, bool>> Erasable() =>
        parcel => parcel.Status == ParcelStatus.Deleted;

    public async Task<ParcelSweepResult> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = 0;

        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var found = await FindExpirableAsync(now, cancellationToken);

            foreach (var parcel in found)
                if (await ExpireAsync(parcel, now, cancellationToken))
                    expired++;

            if (found.Count < _options.SweepBatchSize)
                break;
        }

        var erased = 0;
        var failed = 0;

        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var found = await session
                .Query<Parcel>()
                .Where(Erasable())
                .OrderBy(parcel => parcel.DeletedAt)
                .Take(_options.SweepBatchSize)
                .ToListAsync(cancellationToken);

            var failedBefore = failed;

            foreach (var parcel in found)
                if (await EraseAsync(parcel, cancellationToken))
                    erased++;
                else
                    failed++;

            // A batch the storage failed on would come back whole in the next one; the next sweep tries again
            if (found.Count < _options.SweepBatchSize || failed > failedBefore)
                break;
        }

        if (expired > 0 || erased > 0 || failed > 0)
            LogSwept(expired, erased, failed);

        return new(expired, erased, failed);
    }

    /// <summary>
    ///     One batch of the parcels <see cref="Expirable" /> as of <paramref name="now" />, oldest first, each read
    ///     with its version.
    /// </summary>
    internal async Task<IReadOnlyList<Parcel>> FindExpirableAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await session
            .Query<Parcel>()
            .Where(Expirable(now, _options))
            .OrderBy(parcel => parcel.CreatedAt)
            .Take(_options.SweepBatchSize)
            .ToListAsync(cancellationToken);

    /// <summary>
    ///     Marks <paramref name="parcel" />, as it was read, deleted. Returns <c>false</c>, and writes nothing, when it
    ///     has changed since: sent, completed or discarded after it was found.
    /// </summary>
    internal async Task<bool> ExpireAsync(Parcel parcel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Update(parcel with
        {
            Status = ParcelStatus.Deleted,
            DeletedAt = now,
        });

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            session.EjectAllPendingChanges();
            LogExpiryLost(parcel.Id);

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Deletes the thumbnail, then the original, then the document. Returns <c>false</c> when the storage failed,
    ///     leaving the document for the next sweep.
    /// </summary>
    private async Task<bool> EraseAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        try
        {
            await parcelStore.DeleteAsync(ParcelKeys.Thumbnail(parcel.Id), cancellationToken);
            await parcelStore.DeleteAsync(ParcelKeys.Original(parcel.Id), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogEraseFailed(exception, parcel.Id);

            return false;
        }

        session.Delete(parcel);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (MartenRaces.IsLostRace(exception))
        {
            // Another sweep erased it first
            session.EjectAllPendingChanges();
        }

        return true;
    }

    public async Task<ParcelOrphanResult> ScanOrphansAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var released = await ReleaseDanglingAsync(now, cancellationToken);
        var deleted = await DeleteOrphanBlobsAsync(now, cancellationToken);

        LogScanned(released, deleted);

        return new(released, deleted);
    }

    /// <summary>
    ///     Marks deleted the sent parcels whose packet is gone. Deleting a packet marks its parcel in the same commit,
    ///     so this finds nothing unless some path deletes packets without it.
    /// </summary>
    private async Task<int> ReleaseDanglingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();

        await using (var command = dataSource.CreateCommand(_danglingSql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));

        var released = 0;

        foreach (var id in ids)
        {
            // Read again, for the version: the update must lose to anything that changed the parcel since
            if (await session.LoadAsync<Parcel>(id, cancellationToken) is not
                { Status: ParcelStatus.Uploaded, AttachedPacketId: not null } parcel)
                continue;

            if (!await ExpireAsync(parcel, now, cancellationToken))
                continue;

            LogDanglingReleased(parcel.Id, parcel.AttachedPacketId.Value);
            released++;
        }

        return released;
    }

    /// <summary>
    ///     Deletes the blobs that belong to no parcel. A blob younger than the minimum age is left alone: an upload
    ///     records its parcel before it stores a byte, so only a blob that has had time to be recorded and is not can
    ///     be left over. A name the API never writes is reported and never deleted.
    /// </summary>
    private async Task<int> DeleteOrphanBlobsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bornBefore = now - _options.OrphanBlobMinimumAge;
        var batch = new Dictionary<Guid, List<string>>();
        var deleted = 0;

        await foreach (var blob in parcelStore.ListAsync(cancellationToken))
        {
            if (blob.CreatedOn >= bornBefore)
                continue;

            if (!ParcelKeys.TryParse(blob.Name, out var id))
            {
                LogForeignBlob(blob.Name);

                continue;
            }

            if (!batch.TryGetValue(id, out var names))
                batch[id] = names = [];

            names.Add(blob.Name);

            if (batch.Count >= OrphanCheckBatchSize)
            {
                deleted += await DeleteUnknownAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            deleted += await DeleteUnknownAsync(batch, cancellationToken);

        return deleted;
    }

    private async Task<int> DeleteUnknownAsync(Dictionary<Guid, List<string>> blobs, CancellationToken cancellationToken)
    {
        var known = (await session.LoadManyAsync<Parcel>(cancellationToken, blobs.Keys))
            .Select(parcel => parcel.Id)
            .ToHashSet();

        var deleted = 0;

        foreach (var (id, names) in blobs)
        {
            if (known.Contains(id))
                continue;

            foreach (var name in names)
            {
                await parcelStore.DeleteAsync(name, cancellationToken);
                deleted++;
            }

            LogOrphanBlobsDeleted(id, names.Count);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The parcel sweep expired {Expired}, erased {Erased} and failed to erase {Failed} parcels")]
    private partial void LogSwept(int expired, int erased, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Parcel {ParcelId} changed after the sweep found it expired, and was left as it is")]
    private partial void LogExpiryLost(Guid parcelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to erase the bytes of parcel {ParcelId}; the next sweep tries again")]
    private partial void LogEraseFailed(Exception exception, Guid parcelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The parcel orphan scan released {Released} parcels whose packet was gone and deleted {Deleted} blobs with no parcel")]
    private partial void LogScanned(int released, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parcel {ParcelId} was sent in packet {PacketId}, which is gone, and has been marked deleted")]
    private partial void LogDanglingReleased(Guid parcelId, Guid packetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleted {Count} blobs of parcel {ParcelId}, which has no document")]
    private partial void LogOrphanBlobsDeleted(Guid parcelId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Blob {BlobName} is not named as a parcel blob, and was left alone")]
    private partial void LogForeignBlob(string blobName);
}
