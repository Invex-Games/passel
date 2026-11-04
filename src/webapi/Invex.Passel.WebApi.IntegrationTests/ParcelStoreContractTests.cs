using Azure.Storage.Blobs;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     What the API relies on from a blob store, held against the real one on Azurite and the in-memory one the
///     extra hosts use, so a test passing on the in-memory store means the same as one passing on Azurite. Each test
///     uses keys of its own, and the Azure store a container of its own, so they share Azurite without seeing each
///     other's blobs.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class ParcelStoreContractTests(PasselApiFixture fixture)
{
    private const string Azure = "azure";
    private const string Memory = "memory";

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private IParcelStore Store(string kind) =>
        kind switch
        {
            // A fresh container, which the store creates on the first put as it does on a fresh Azurite
            Azure => new AzureBlobParcelStore(new BlobServiceClient(fixture.BlobConnectionString),
                Microsoft.Extensions.Options.Options.Create(new ParcelOptions
                {
                    ContainerName = $"contract-{Guid.NewGuid():N}",
                })),
            Memory => new InMemoryParcelStore(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static string NewKey() =>
        ParcelKeys.Original(Guid.CreateVersion7());

    private static async Task PutAsync(IParcelStore store, string key, byte[] bytes) =>
        await store.PutAsync(key, new MemoryStream(bytes), "application/octet-stream", CancellationToken);

    private static async Task<byte[]> ReadAllAsync(ParcelContent content)
    {
        await using var stream = content.Stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, CancellationToken);

        return buffer.ToArray();
    }

    [Theory]
    [InlineData(Azure)]
    [InlineData(Memory)]
    public async Task What_is_put_reads_back_seekable_with_its_length(string kind)
    {
        var store = Store(kind);
        var key = NewKey();
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();

        await PutAsync(store, key, bytes);

        var content = await store.OpenReadAsync(key, CancellationToken);

        Assert.NotNull(content);
        Assert.True(content.Stream.CanSeek);
        Assert.Equal(bytes.Length, content.Length);

        // Range requests seek and read the stream's own length
        Assert.Equal(bytes.Length, content.Stream.Length);
        content.Stream.Seek(1000, SeekOrigin.Begin);
        var tail = new byte[10];
        await content.Stream.ReadExactlyAsync(tail, CancellationToken);
        Assert.Equal(bytes[1000..1010], tail);

        content.Stream.Seek(0, SeekOrigin.Begin);
        Assert.Equal(bytes, await ReadAllAsync(content));
    }

    [Theory]
    [InlineData(Azure)]
    [InlineData(Memory)]
    public async Task A_put_never_overwrites(string kind)
    {
        var store = Store(kind);
        var key = NewKey();

        await PutAsync(store, key, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PutAsync(store, key, [4, 5, 6]));

        var content = await store.OpenReadAsync(key, CancellationToken);

        Assert.NotNull(content);
        Assert.Equal([1, 2, 3], await ReadAllAsync(content));
    }

    [Theory]
    [InlineData(Azure)]
    [InlineData(Memory)]
    public async Task A_blob_that_is_not_there_reads_as_null(string kind)
    {
        var store = Store(kind);

        // Before anything is stored (for Azure, before the container exists) and after
        Assert.Null(await store.OpenReadAsync(NewKey(), CancellationToken));

        await PutAsync(store, NewKey(), [1]);

        Assert.Null(await store.OpenReadAsync(NewKey(), CancellationToken));
    }

    [Theory]
    [InlineData(Azure)]
    [InlineData(Memory)]
    public async Task A_delete_is_idempotent(string kind)
    {
        var store = Store(kind);
        var key = NewKey();

        // Nothing to delete yet: for Azure, not even the container
        await store.DeleteAsync(key, CancellationToken);

        await PutAsync(store, key, [1, 2, 3]);

        await store.DeleteAsync(key, CancellationToken);
        await store.DeleteAsync(key, CancellationToken);

        Assert.Null(await store.OpenReadAsync(key, CancellationToken));
    }

    [Theory]
    [InlineData(Azure)]
    [InlineData(Memory)]
    public async Task The_list_has_what_was_put_with_when_and_not_what_was_deleted(string kind)
    {
        var store = Store(kind);
        var id = Guid.CreateVersion7();
        var gone = NewKey();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Nothing is listed before anything is stored
        Assert.Empty(await store.ListAsync(CancellationToken).ToListAsync(CancellationToken));

        await PutAsync(store, ParcelKeys.Original(id), [1]);
        await PutAsync(store, ParcelKeys.Thumbnail(id), [2]);
        await PutAsync(store, gone, [3]);
        await store.DeleteAsync(gone, CancellationToken);

        var listed = await store.ListAsync(CancellationToken).ToListAsync(CancellationToken);

        Assert.Equal([ParcelKeys.Original(id), ParcelKeys.Thumbnail(id)], listed.Select(blob => blob.Name).Order());
        Assert.All(listed, blob => Assert.InRange(blob.CreatedOn, before, DateTimeOffset.UtcNow.AddMinutes(5)));

        // Both names lead back to the parcel
        Assert.All(listed, blob =>
        {
            Assert.True(ParcelKeys.TryParse(blob.Name, out var parsed));
            Assert.Equal(id, parsed);
        });
    }
}
