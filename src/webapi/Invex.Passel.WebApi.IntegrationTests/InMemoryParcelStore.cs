using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A blob store in memory, for the API hosts a test starts beside the shared one (an empty database, a migrated
///     schema), which have no Azurite container of their own. It keeps the contract of the real store, which
///     <see cref="ParcelStoreContractTests" /> holds both to: a put never overwrites, a read is seekable with its
///     length known, and a delete of nothing succeeds.
/// </summary>
internal sealed class InMemoryParcelStore(TimeProvider? time = null) : IParcelStore
{
    private readonly ConcurrentDictionary<string, StoredBytes> _blobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        if (!_blobs.TryAdd(key, new(buffer.ToArray(), contentType, _time.GetUtcNow())))
            throw new InvalidOperationException($"A blob named {key} exists already; parcels are never overwritten.");
    }

    public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.TryGetValue(key, out var blob)
            ? new ParcelContent(new MemoryStream(blob.Bytes, writable: false), blob.Bytes.Length)
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _blobs.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredBlob> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        foreach (var (name, blob) in _blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new(name, blob.Created);
        }
    }

    private sealed record StoredBytes(byte[] Bytes, string ContentType, DateTimeOffset Created);
}
