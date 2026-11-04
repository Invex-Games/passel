using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How uploaded files are stored, checked, thumbnailed and swept, bound from the <c>Parcels</c> configuration
///     section. The storage account itself comes from the <c>passel-blobs</c> connection string; the per-user limits
///     are in <see cref="QuotaOptions" />.
/// </summary>
internal sealed class ParcelOptions : IValidatableObject
{
    public const string SectionName = "Parcels";

    /// <summary>
    ///     The blob container the files are stored in.
    /// </summary>
    [Required]
    public string ContainerName { get; set; } = "parcels";

    /// <summary>
    ///     The largest file one upload may carry. Kestrel refuses bodies over about 30 MB unless
    ///     <c>Kestrel:Limits:MaxRequestBodySize</c> is raised as well; the upload endpoint raises the limit for its own
    ///     requests to this plus room for the multipart framing.
    /// </summary>
    [Range(1, 1_073_741_824)]
    public long MaxParcelBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    ///     How long an uploaded file waits to be sent in a packet before it is deleted.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan UnattachedLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     How long an upload may stay unfinished before it is taken to have failed (the instance stopped part way)
    ///     and deleted. Shorter than <see cref="UnattachedLifetime" />, so an upload still in flight is never expired
    ///     as unsent first.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan UploadingGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Whether the sweep runs at all. Off, deleted and expired files keep their bytes until it is on again.
    /// </summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>
    ///     How often the sweep expires unsent files and erases deleted ones when nothing wakes it sooner. Deleting a
    ///     file or its packet wakes it at once.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How often the sweep also looks for what the normal path missed: stored bytes with no file, and files whose
    ///     packet is gone.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan OrphanScanInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    ///     How old stored bytes with no file must be before the orphan scan deletes them. An upload writes its file
    ///     before its bytes, so anything this old with no file is left over rather than in flight.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan OrphanBlobMinimumAge { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    ///     How many files the sweep expires or erases per batch.
    /// </summary>
    [Range(1, 1000)]
    public int SweepBatchSize { get; set; } = 100;

    /// <summary>
    ///     The longest side of a thumbnail, in pixels. Smaller images are not enlarged.
    /// </summary>
    [Range(64, 2048)]
    public int ThumbnailSize { get; set; } = 512;

    /// <summary>
    ///     The most pixels an image may have to be decoded for a thumbnail. Larger ones are stored and served as plain
    ///     files. This is what bounds the memory and time a decode takes: a PNG or GIF decodes at full size, four
    ///     bytes a pixel.
    /// </summary>
    [Range(1, 268_435_456)]
    public long MaxImagePixels { get; set; } = 40_000_000;

    /// <summary>
    ///     The longest side an image may have to be decoded for a thumbnail.
    /// </summary>
    [Range(1, 65_535)]
    public int MaxImageDimension { get; set; } = 16_384;

    /// <summary>
    ///     How many uploads one instance takes at the same time. Others wait briefly, then get 503.
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrentUploads { get; set; } = 4;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UploadingGrace >= UnattachedLifetime)
            yield return new($"{nameof(UploadingGrace)} must be shorter than {nameof(UnattachedLifetime)}, so an " +
                "upload still in flight is never expired as unsent",
                [nameof(UploadingGrace), nameof(UnattachedLifetime)]);
    }
}
