using System.Globalization;
using Npgsql;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What a user has in uploaded files: how many are unsent (uploads in progress included), and the bytes of every
///     file not deleted, sent or not, with an upload in progress counted at the size it reserved.
/// </summary>
internal readonly record struct ParcelQuotaUsage(long Pending, long Bytes);

internal interface IParcelQuotaService
{
    Task<ParcelQuotaUsage> GetUsageAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Reads a user's file usage with one aggregate over the parcel table, through the index on the user id. An upload
///     reserves its size in the table before reading the body, so uploads that follow it see it counted; only two
///     uploads checked in the same few milliseconds can both pass, so the quotas are approximate by an upload, as the
///     packet quotas are by a packet.
/// </summary>
internal sealed class ParcelQuotaService(NpgsqlDataSource dataSource, IDocumentStore store) : IParcelQuotaService
{
    // The status is stored as its number (the store has no string enum converter), and an unsent parcel's packet as
    // JSON null, which ->> reads as SQL null just as it would a missing key. The filter matches the expression of
    // the index on the user id that ConfigureStore declares on the parcel document
    private readonly string _sql = $"""
        select
            count(*) filter (where (d.data ->> '{nameof(Parcel.Status)}')::int <> {(int)ParcelStatus.Deleted}
                and d.data ->> '{nameof(Parcel.AttachedPacketId)}' is null),
            coalesce(sum((d.data ->> '{nameof(Parcel.Size)}')::bigint)
                filter (where (d.data ->> '{nameof(Parcel.Status)}')::int <> {(int)ParcelStatus.Deleted}), 0)::bigint
        from {store.Options.FindOrResolveDocumentType(typeof(Parcel)).TableName.QualifiedName} as d
        where d.data ->> '{nameof(Parcel.UserId)}' = $1
        """;

    public async Task<ParcelQuotaUsage> GetUsageAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(_sql);
        command.Parameters.Add(new() { Value = userId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new(reader.GetInt64(0), reader.GetInt64(1));
    }
}

internal static class ParcelQuota
{
    private const long Megabyte = 1024 * 1024;

    /// <summary>
    ///     Decides whether a file of <paramref name="addingBytes" /> may be added to <paramref name="usage" />:
    ///     <c>null</c> when it fits, otherwise the refusal. The unsent count is checked first, so a user at both
    ///     limits is told to send or delete what is waiting rather than that their storage is full.
    /// </summary>
    internal static QuotaExceeded? Exceeded(ParcelQuotaUsage usage, long addingBytes, QuotaOptions quotas)
    {
        if (usage.Pending >= quotas.MaxPendingParcelsPerUser)
            return new(StatusCodes.Status429TooManyRequests,
                "Too many unsent files",
                $"You have {Count(usage.Pending)} files uploaded but not sent. Send or delete them, or wait an hour for them to expire.");

        if (usage.Bytes + addingBytes > quotas.MaxParcelBytesPerUser)
            return new(StatusCodes.Status413PayloadTooLarge,
                "Storage limit reached",
                $"Your files use {Size(usage.Bytes)} of {Size(quotas.MaxParcelBytesPerUser)}. Delete some files and try again.");

        return null;
    }

    /// <summary>
    ///     The refusal of a file over <see cref="ParcelOptions.MaxParcelBytes" />.
    /// </summary>
    internal static QuotaExceeded TooLarge(ParcelOptions options) =>
        new(StatusCodes.Status413PayloadTooLarge,
            "File too large",
            $"Files can be at most {Size(options.MaxParcelBytes)}.");

    private static string Count(long count) =>
        count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Size(long bytes) =>
        bytes >= Megabyte
            ? $"{Count(bytes / Megabyte)} MB"
            : $"{Count(bytes / 1024)} KB";
}
