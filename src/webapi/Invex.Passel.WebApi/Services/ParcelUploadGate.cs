using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     How many uploads this instance takes at once (<see cref="ParcelOptions.MaxConcurrentUploads" />). Each holds a
///     buffered file on the temporary disk and may decode an image, so the rest wait briefly for a place, then are
///     answered with 503 and asked to retry.
/// </summary>
internal sealed class ParcelUploadGate(IOptions<ParcelOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _places =
        new(options.Value.MaxConcurrentUploads, options.Value.MaxConcurrentUploads);

    /// <summary>
    ///     Waits up to <paramref name="timeout" /> for a place. <c>true</c> when the caller has one, which it must give
    ///     back with <see cref="Release" />.
    /// </summary>
    public Task<bool> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _places.WaitAsync(timeout, cancellationToken);

    public void Release() =>
        _places.Release();

    public void Dispose() =>
        _places.Dispose();
}
