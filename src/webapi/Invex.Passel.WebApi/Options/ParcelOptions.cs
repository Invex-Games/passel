using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     Where the bytes of uploaded files are kept, and how the files are checked, thumbnailed and swept, bound from
///     the <c>Parcels</c> configuration section. <see cref="Store" /> picks the store: a directory on the local disk
///     (<see cref="FileSystemRoot" />), or the Azure storage account behind the <c>passel-blobs</c> connection string,
///     which is also what <see cref="ParcelStoreKind.Auto" /> picks when that connection string is set. The per-user
///     limits are in <see cref="QuotaOptions" />.
/// </summary>
internal sealed class ParcelOptions : IValidatableObject
{
    public const string SectionName = "Parcels";

    /// <summary>
    ///     Which store holds the bytes. <see cref="ParcelStoreKind.Auto" /> keeps them in Azure when the
    ///     <c>passel-blobs</c> connection string is set and has no store otherwise, so every file request answers 503.
    /// </summary>
    public ParcelStoreKind Store { get; set; } = ParcelStoreKind.Auto;

    /// <summary>
    ///     The directory the files are kept in when <see cref="Store" /> is <see cref="ParcelStoreKind.FileSystem" />:
    ///     an absolute path on a local disk, never a network share. It is created on the first upload if it is not
    ///     there.
    /// </summary>
    public string? FileSystemRoot { get; set; }

    /// <summary>
    ///     Whether the <c>passel-blobs</c> connection string is set, which an explicit
    ///     <see cref="ParcelStoreKind.AzureBlob" /> needs. Filled in from the connection strings when the options are
    ///     configured, never bound from the section.
    /// </summary>
    internal bool BlobConnectionConfigured { get; set; }

    /// <summary>
    ///     The blob container the files are stored in, when they are stored in Azure.
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
    ///     How long an upload waits for the one decode an instance runs at a time before it is stored without a
    ///     thumbnail instead. A decode cannot be cancelled, so this is what keeps one slow image from holding every
    ///     upload behind it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan DecodeSlotTimeout { get; set; } = TimeSpan.FromSeconds(5);

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

        // Fully qualified rather than merely rooted: on Windows a path such as \parcels is rooted and still depends on
        // the current drive
        if (Store == ParcelStoreKind.FileSystem &&
            (string.IsNullOrWhiteSpace(FileSystemRoot) || !Path.IsPathFullyQualified(FileSystemRoot)))
            yield return new($"{nameof(Store)} {nameof(ParcelStoreKind.FileSystem)} needs {nameof(FileSystemRoot)} " +
                "set to an absolute path", [nameof(FileSystemRoot)]);

        // No silent fallback to no storage at all: a store asked for by name must be there
        if (Store == ParcelStoreKind.AzureBlob && !BlobConnectionConfigured)
            yield return new($"{nameof(Store)} {nameof(ParcelStoreKind.AzureBlob)} needs the passel-blobs " +
                "connection string", [nameof(Store)]);
    }
}

/// <summary>
///     The stores <see cref="ParcelOptions.Store" /> chooses between.
/// </summary>
internal enum ParcelStoreKind
{
    /// <summary>
    ///     Azure when the <c>passel-blobs</c> connection string is set, and no store otherwise: what every host did
    ///     before there was a choice, and what the AppHost and the Azure deployment still rely on.
    /// </summary>
    Auto,

    /// <summary>
    ///     The Azure storage account behind the <c>passel-blobs</c> connection string, which must be set.
    /// </summary>
    AzureBlob,

    /// <summary>
    ///     A directory on the local disk, <see cref="ParcelOptions.FileSystemRoot" />.
    /// </summary>
    FileSystem,
}
