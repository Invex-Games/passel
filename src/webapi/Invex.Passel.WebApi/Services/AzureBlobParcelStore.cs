using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Keeps the bytes of uploaded files as block blobs in one container of the storage account behind the
///     <c>passel-blobs</c> connection: Azurite locally and in the tests, an Azure storage account when deployed. Blob
///     soft delete and versioning are off on the account, so a deleted blob is gone (ADR 0001).
/// </summary>
internal sealed class AzureBlobParcelStore(BlobServiceClient service, IOptions<ParcelOptions> options) : IParcelStore
{
    // Blocks of 4 MiB, two in flight: a 25 MB file goes up in seven blocks without holding more than two in memory
    private const int TransferSize = 4 * 1024 * 1024;

    // What a download reads from storage at a time
    private const int ReadBufferSize = 1024 * 1024;

    private readonly SemaphoreSlim _containerLock = new(1, 1);
    private volatile bool _containerExists;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync();

        try
        {
            await container
                .GetBlobClient(key)
                .UploadAsync(content,
                    new BlobUploadOptions
                    {
                        HttpHeaders = new()
                        {
                            ContentType = contentType,
                        },

                        // Never overwrite: an existing blob under a new parcel's name would be a bug, not an update
                        Conditions = new()
                        {
                            IfNoneMatch = ETag.All,
                        },
                        TransferOptions = new()
                        {
                            MaximumConcurrency = 2,
                            InitialTransferSize = TransferSize,
                            MaximumTransferSize = TransferSize,
                        },
                    },
                    cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
            throw new InvalidOperationException($"A blob named {key} exists already; parcels are never overwritten.",
                exception);
        }
    }

    public async Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var blob = Container().GetBlobClient(key);

        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);

            // Pinned to the version just read, which never changes anyway: blobs are written once
            var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false)
                {
                    BufferSize = ReadBufferSize,
                    Conditions = new()
                    {
                        IfMatch = properties.Value.ETag,
                    },
                },
                cancellationToken);

            return new(stream, properties.Value.ContentLength);
        }
        catch (RequestFailedException exception) when (exception.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await Container()
                .GetBlobClient(key)
                .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            // Nothing was ever stored, so there is nothing to delete
        }
    }

    public async IAsyncEnumerable<StoredBlob> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var container = Container();

        if (!await container.ExistsAsync(cancellationToken))
            yield break;

        await foreach (var item in container.GetBlobsAsync(cancellationToken: cancellationToken))
            yield return new(item.Name, item.Properties.CreatedOn ?? DateTimeOffset.MinValue);
    }

    private BlobContainerClient Container() =>
        service.GetBlobContainerClient(options.Value.ContainerName);

    /// <summary>
    ///     The container, created first if this instance has not seen it yet: the deployment provisions it, but a
    ///     fresh Azurite has none. Only a creation that succeeded is remembered, so one storage hiccup on the first
    ///     upload does not fail every later one, and it runs without the request's token, so one aborted upload does
    ///     not cancel it for the others waiting on it.
    /// </summary>
    private async Task<BlobContainerClient> GetContainerAsync()
    {
        var container = Container();

        if (_containerExists)
            return container;

        await _containerLock.WaitAsync(CancellationToken.None);

        try
        {
            if (!_containerExists)
            {
                await container.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
                _containerExists = true;
            }
        }
        finally
        {
            _containerLock.Release();
        }

        return container;
    }
}
