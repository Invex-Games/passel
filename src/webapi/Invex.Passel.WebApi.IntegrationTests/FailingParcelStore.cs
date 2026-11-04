using Azure;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A blob store that is down: every call fails as the storage SDK fails when the service answers 503.
/// </summary>
internal sealed class FailingParcelStore : IParcelStore
{
    private static RequestFailedException Down() =>
        new(503, "The server is busy.", "ServerBusy", null);

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        throw Down();

    public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        throw Down();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw Down();

    public IAsyncEnumerable<StoredBlob> ListAsync(CancellationToken cancellationToken) =>
        throw Down();
}
