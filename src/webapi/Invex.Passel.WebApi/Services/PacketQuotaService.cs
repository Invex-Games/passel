using System.Globalization;
using Npgsql;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What a user has stored: how many packets, and the stored size of their packet documents in bytes.
/// </summary>
internal readonly record struct PacketQuotaUsage(long Packets, long Bytes);

/// <summary>
///     Why a create is refused: the status to answer with and a message for the user.
/// </summary>
internal sealed record QuotaExceeded(int StatusCode, string Title, string Detail);

internal interface IPacketQuotaService
{
    Task<PacketQuotaUsage> GetUsageAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Reads a user's usage with one aggregate over the packet snapshot table, through the index on the user id.
///     Two creates racing each other can both pass the check, so the quotas are approximate by a packet.
/// </summary>
internal sealed class PacketQuotaService(NpgsqlDataSource dataSource, IDocumentStore store) : IPacketQuotaService
{
    // The filter matches the leading expression of the index on user id and id that Program.cs declares on the
    // snapshot document, so only the user's rows are read. pg_column_size reads the stored (compressed) size without
    // detoasting the document.
    private readonly string _sql = $"""
        select count(*), coalesce(sum(pg_column_size(d.data)), 0)::bigint
        from {store.Options.FindOrResolveDocumentType(typeof(PacketAggregate)).TableName.QualifiedName} as d
        where d.data ->> '{nameof(PacketAggregate.UserId)}' = $1
        """;

    public async Task<PacketQuotaUsage> GetUsageAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(_sql);
        command.Parameters.Add(new() { Value = userId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new(reader.GetInt64(0), reader.GetInt64(1));
    }
}

internal static class PacketQuota
{
    /// <summary>
    ///     Decides whether a packet of <paramref name="packetBytes" /> may be added to <paramref name="usage" />:
    ///     <c>null</c> when it fits, otherwise the refusal. The packet count is checked first, so a user at both
    ///     limits is told to delete packets rather than to send less.
    /// </summary>
    internal static QuotaExceeded? Exceeded(PacketQuotaUsage usage, long packetBytes, QuotaOptions quotas)
    {
        if (usage.Packets >= quotas.MaxPacketsPerUser)
            return new(StatusCodes.Status429TooManyRequests,
                "Packet limit reached",
                $"You have {Count(usage.Packets)} of the {Count(quotas.MaxPacketsPerUser)} packets allowed. Delete some to send more.");

        if (usage.Bytes + packetBytes > quotas.MaxBytesPerUser)
            return new(StatusCodes.Status413PayloadTooLarge,
                "Storage limit reached",
                $"This packet would take your packets past the {Size(quotas.MaxBytesPerUser)} allowed. Delete some to send more.");

        return null;
    }

    private static string Count(long count) =>
        count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{Count(bytes / (1024 * 1024))} MB"
            : $"{Count(bytes / 1024)} KB";
}
