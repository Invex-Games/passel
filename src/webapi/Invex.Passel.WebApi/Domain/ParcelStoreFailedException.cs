namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     The file store on the local disk failed to <see cref="Operation" /> the blob named <see cref="Key" />: the disk
///     is full, the directory is not writable, or the device failed. Answered with 503 like an outage of a storage
///     service. The failure itself is the inner exception, whose message names the path on disk and is never logged.
/// </summary>
internal sealed class ParcelStoreFailedException(string operation, string key, Exception innerException)
    : Exception($"File storage failed to {operation} {key}.", innerException)
{
    /// <summary>
    ///     What the store was doing: write, read, delete or list.
    /// </summary>
    public string Operation { get; } = operation;

    /// <summary>
    ///     The blob it was doing it to, or the empty string for a list.
    /// </summary>
    public string Key { get; } = key;
}
