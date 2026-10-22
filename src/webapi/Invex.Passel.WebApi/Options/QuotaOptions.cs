using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How much a user may store, bound from the <c>Quotas</c> configuration section. Checked before every create.
/// </summary>
internal sealed class QuotaOptions
{
    public const string SectionName = "Quotas";

    /// <summary>
    ///     The most packets a user may have at once.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxPacketsPerUser { get; set; } = 5000;

    /// <summary>
    ///     The most bytes a user may have stored across their packets, measured as the stored size of their packet
    ///     documents (PostgreSQL compresses large ones).
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxBytesPerUser { get; set; } = 50 * 1024 * 1024;
}
