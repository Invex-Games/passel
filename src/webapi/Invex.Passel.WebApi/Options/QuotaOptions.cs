using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How much a user may store, bound from the <c>Quotas</c> configuration section. The packet quotas are checked
///     before every create, the device cap before every registration of a new installation, and the file quotas before
///     every upload.
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
    ///     documents only (PostgreSQL compresses large ones). Uploaded files count against
    ///     <see cref="MaxParcelBytesPerUser" /> instead.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxBytesPerUser { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    ///     The most bytes a user may have in uploaded files at once: every file not deleted, sent or not, with an
    ///     upload in progress counted at the size it reserved. Checked before every upload.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxParcelBytesPerUser { get; set; } = 500L * 1024 * 1024;

    /// <summary>
    ///     The most files a user may have uploaded and not yet sent, uploads in progress included. Unsent files expire
    ///     after <c>Parcels:UnattachedLifetime</c>, so this bounds what an abandoned client can leave behind.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPendingParcelsPerUser { get; set; } = 20;

    /// <summary>
    ///     The most devices a user may have registered at once. The device list is not paged, so this also bounds
    ///     its length.
    /// </summary>
    [Range(1, 1000)]
    public int MaxDevicesPerUser { get; set; } = 20;
}
