namespace Invex.Passel.WebApi.Services;

/// <summary>
///     The store on a host started without file storage, such as the <c>db-migrate</c> job: the host still builds,
///     and every use of the store throws a <see cref="ParcelStoreUnavailableException" />.
/// </summary>
internal sealed class UnavailableParcelStore : IParcelStore
{
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        throw new ParcelStoreUnavailableException();

    public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        throw new ParcelStoreUnavailableException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw new ParcelStoreUnavailableException();

    public IAsyncEnumerable<StoredBlob> ListAsync(CancellationToken cancellationToken) =>
        throw new ParcelStoreUnavailableException();
}
