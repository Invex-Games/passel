using System.Runtime.CompilerServices;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A blob store whose writes fail as a block upload fails when the sweep erased its parcel mid-upload: the commit
///     names blocks that are gone. Reads, deletes and listing behave as an empty store.
/// </summary>
internal sealed class DiscardingParcelStore : IParcelStore
{
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        throw new ParcelWriteDiscardedException(key, new InvalidOperationException("InvalidBlockList"));

    public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<ParcelContent?>(null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<StoredBlob> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        yield break;
    }
}
