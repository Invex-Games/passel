namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Where the bytes of uploaded files are kept: one blob per original and one per thumbnail, named by
///     <see cref="ParcelKeys" />. The parcel documents say what each blob is; the store knows only names and bytes.
/// </summary>
internal interface IParcelStore
{
    /// <summary>
    ///     Writes a new blob and never overwrites one: parcels are immutable, so an existing key is a bug, and is
    ///     thrown as an <see cref="InvalidOperationException" />.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    ///     A seekable stream over the blob with its length known, or <c>null</c> when there is no such blob. The
    ///     caller disposes the stream.
    /// </summary>
    Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes the blob. Idempotent: a blob that is not there is success.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    ///     Every blob, for the orphan scan.
    /// </summary>
    IAsyncEnumerable<StoredBlob> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
///     A blob opened for reading: a seekable stream and its length.
/// </summary>
internal sealed record ParcelContent(Stream Stream, long Length);

/// <summary>
///     A blob as the orphan scan sees it.
/// </summary>
internal sealed record StoredBlob(string Name, DateTimeOffset CreatedOn);

/// <summary>
///     The blob names of a parcel, derived from its id alone so there is no stored name to drift from it. Names never
///     carry a user id or a file name, because the storage SDK records blob URIs in its logs and traces.
/// </summary>
internal static class ParcelKeys
{
    private const string ThumbnailSuffix = ".thumb";

    public static string Original(Guid id) =>
        $"{id:N}";

    public static string Thumbnail(Guid id) =>
        $"{id:N}{ThumbnailSuffix}";

    /// <summary>
    ///     The parcel a blob belongs to, from either of its names; <c>false</c> for a name the API never writes.
    /// </summary>
    public static bool TryParse(string name, out Guid id)
    {
        var original = name.EndsWith(ThumbnailSuffix, StringComparison.Ordinal)
            ? name[..^ThumbnailSuffix.Length]
            : name;

        return Guid.TryParseExact(original, "N", out id);
    }
}
