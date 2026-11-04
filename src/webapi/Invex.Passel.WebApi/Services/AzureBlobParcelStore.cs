using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
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
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.InvalidBlockList)
        {
            // The commit named blocks that DeleteAsync discarded: the sweep erased this parcel mid-upload
            throw new ParcelWriteDiscardedException(key, exception);
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

    /// <summary>
    ///     Deletes the blob, and when there is none, the blocks an upload cut off part way staged under its name
    ///     without committing them: those are no blob to delete, and Azure would keep them for about a week. A parcel
    ///     is only erased once it is deleted, so an upload still running under the name has had its parcel failed or
    ///     expired already; it then fails its own put, its blocks gone.
    /// </summary>
    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var blob = Container().GetBlockBlobClient(key);

        try
        {
            var deleted = await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken);

            if (!deleted.Value)
                await DiscardUncommittedAsync(blob, cancellationToken);
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

        // With the names that hold only uncommitted blocks, so the orphan scan erases what a cut-off upload staged.
        // Such a name may have no creation time; it then counts as old, and only its parcel, recorded before the
        // upload stages a block, keeps it from being deleted while the upload runs
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.Uncommitted, prefix: null,
                           cancellationToken: cancellationToken))
            yield return new(item.Name,
                item.Properties.CreatedOn ?? item.Properties.LastModified ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    ///     Discards the uncommitted blocks staged under a name with no blob: committing an empty block list replaces
    ///     them with an empty blob, which is then deleted. Costs one request when there is nothing staged.
    /// </summary>
    private static async Task DiscardUncommittedAsync(BlockBlobClient blob, CancellationToken cancellationToken)
    {
        try
        {
            var blocks = await blob.GetBlockListAsync(BlockListTypes.Uncommitted, cancellationToken: cancellationToken);

            if (!blocks.Value.UncommittedBlocks.Any())
                return;
        }
        catch (RequestFailedException exception) when (exception.Status == StatusCodes.Status404NotFound &&
                                                       exception.ErrorCode != BlobErrorCode.ContainerNotFound)
        {
            // Nothing staged under the name either
            return;
        }

        try
        {
            // Only where there is still no blob: one committed since is deleted below all the same
            await blob.CommitBlockListAsync([],
                new CommitBlockListOptions
                {
                    Conditions = new()
                    {
                        IfNoneMatch = ETag.All,
                    },
                },
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
        }

        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
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
