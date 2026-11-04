using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How much a user may store, bound from the <c>Quotas</c> configuration section. The packet quotas are checked
///     before every create, the device cap before every registration of a new installation.
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

    /// <summary>
    ///     The most devices a user may have registered at once. The device list is not paged, so this also bounds
    ///     its length.
    /// </summary>
    [Range(1, 1000)]
    public int MaxDevicesPerUser { get; set; } = 20;
}
