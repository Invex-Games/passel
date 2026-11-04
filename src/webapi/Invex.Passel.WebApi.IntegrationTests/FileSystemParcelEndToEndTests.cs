using System.Net;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Invex.Passel.WebApi.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Files through the real API on the filesystem store a self-hosted deployment uses, against a database of its
///     own: uploading a file and an image, downloading both and the thumbnail, then deleting them and letting the sweep
///     and the orphan scan clear the disk. The compose stack has no way to sign in without Google, so this is where an
///     upload through the API meets that store.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class FileSystemParcelEndToEndTests(PasselApiFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "passel-filesystem-api", Guid.NewGuid().ToString("N"));

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A stream a failed test left open; the temp directory is cleared eventually anyway
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Every file under the root, temps included, by its path relative to the root.
    /// </summary>
    private string[] FilesOnDisk() =>
        Directory.Exists(_root)
            ? Directory
                .GetFiles(_root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    [Fact]
    public async Task Files_go_to_the_disk_come_back_byte_for_byte_and_leave_it_when_erased()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync("passel_parcels_filesystem", CancellationToken);
        var store = new FileSystemParcelStore(Microsoft.Extensions.Options.Options.Create(new ParcelOptions
            {
                Store = ParcelStoreKind.FileSystem,
                FileSystemRoot = _root,
            }),
            NullLogger<FileSystemParcelStore>.Instance);

        await using var api = new PasselApiFactory(connectionString, parcelStore: store);
        using var alice = api.CreateClientFor(PasselApiFixture.User("alice"));
        var encoding = api.Services.GetRequiredService<IIdEncodingService>();

        Guid Decode(ParcelJson parcel) =>
            encoding.TryDecode(parcel.ParcelId, out var id)
                ? id
                : throw new InvalidOperationException($"{parcel.ParcelId} is not an id.");

        var png = TestImages.Png(600, 300);
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0;

        var image = await alice.UploadAsync(png, "photo.png", "image/png", CancellationToken);
        var file = await alice.UploadAsync(bytes, "data.bin", "application/octet-stream", CancellationToken);

        Assert.True(image.IsImage);
        Assert.False(file.IsImage);

        // The image's original and thumbnail and the file's original, named by their ids alone, and no temp left
        Assert.Equal(new[]
            {
                ParcelKeys.Original(Decode(image)),
                ParcelKeys.Thumbnail(Decode(image)),
                ParcelKeys.Original(Decode(file)),
            }.Order(StringComparer.Ordinal),
            FilesOnDisk());

        using (var download = await alice.DownloadParcelAsync(image.ParcelId, CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(png, await download.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        using (var download = await alice.DownloadParcelAsync(file.ParcelId, CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        // A range is read by seeking the file
        using (var range = await alice.DownloadParcelAsync(file.ParcelId, CancellationToken,
                   request => request.Headers.Range = new(1000, 1009)))
        {
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(bytes[1000..1010], await range.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        using (var thumbnail = await alice.GetThumbnailAsync(image.ParcelId, CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
            Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);

            var webp = await thumbnail.Content.ReadAsByteArrayAsync(CancellationToken);
            using var decoded = SkiaSharp.SKBitmap.Decode(webp);

            Assert.Equal((512, 256), (decoded.Width, decoded.Height));
            Assert.Equal(webp,
                await File.ReadAllBytesAsync(Path.Combine(_root, ParcelKeys.Thumbnail(Decode(image))), CancellationToken));
        }

        // The image is sent and its packet deleted; the file is discarded unsent
        var packet = await alice.SendFileAsync(image.ParcelId, null, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(packet.Id, CancellationToken));
        Assert.Equal(HttpStatusCode.NoContent, await alice.DiscardParcelAsync(file.ParcelId, CancellationToken));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var result = await scope
                .ServiceProvider
                .GetRequiredService<ParcelSweep>()
                .RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken);

            Assert.Equal(new ParcelSweepResult(0, 2, 0), result);
        }

        Assert.Empty(FilesOnDisk());

        using (var gone = await alice.DownloadParcelAsync(image.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Bytes no parcel names, as a crash between storing them and recording the parcel leaves them, go once they
        // are older than the orphan scan's minimum age; a finished blob and a temp alike
        var orphan = ParcelKeys.Original(Guid.CreateVersion7());
        await store.PutAsync(orphan, new MemoryStream([1, 2, 3]), "application/octet-stream", CancellationToken);

        var cutOff = Path.Combine(_root, ".uploading", $"{ParcelKeys.Original(Guid.CreateVersion7())}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(cutOff, [4], CancellationToken);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var result = await scope
                .ServiceProvider
                .GetRequiredService<ParcelSweep>()
                .ScanOrphansAsync(DateTimeOffset.UtcNow.AddHours(3), CancellationToken);

            Assert.Equal(new ParcelOrphanResult(0, 2), result);
        }

        Assert.Empty(FilesOnDisk());
    }
}
