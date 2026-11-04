using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     What only the filesystem store has to get right, beside the contract all the stores keep
///     (<see cref="ParcelStoreContractTests" />): the temps of uploads cut off part way, a delete racing an upload, keys
///     that would lead out of the root, and a root that is not there yet. Each test has a directory of its own under
///     the temp directory, and needs neither Docker nor the shared host.
/// </summary>
public sealed class FileSystemParcelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "passel-filesystem", Guid.NewGuid().ToString("N"));

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private string UploadingDirectory =>
        Path.Combine(_root, ".uploading");

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

    private FileSystemParcelStore Store() =>
        new(Microsoft.Extensions.Options.Options.Create(new ParcelOptions
            {
                Store = ParcelStoreKind.FileSystem,
                FileSystemRoot = _root,
            }),
            NullLogger<FileSystemParcelStore>.Instance);

    private static async Task PutAsync(IParcelStore store, string key, byte[] bytes) =>
        await store.PutAsync(key, new MemoryStream(bytes), "application/octet-stream", CancellationToken);

    private static async Task<byte[]?> ReadAsync(IParcelStore store, string key)
    {
        if (await store.OpenReadAsync(key, CancellationToken) is not { } content)
            return null;

        await using var stream = content.Stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, CancellationToken);

        return buffer.ToArray();
    }

    /// <summary>
    ///     A temp as an upload cut off part way leaves it, last written at <paramref name="lastWrite" />.
    /// </summary>
    private string LeaveTemp(string key, DateTime lastWrite)
    {
        Directory.CreateDirectory(UploadingDirectory);

        var path = Path.Combine(UploadingDirectory, $"{key}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWrite);

        return path;
    }

    private string[] Temps() =>
        Directory.Exists(UploadingDirectory)
            ? Directory.GetFiles(UploadingDirectory)
            : [];

    [Fact]
    public async Task The_temp_of_a_cut_off_upload_is_listed_under_its_key_and_deleted_with_it_alone()
    {
        var store = Store();
        var id = Guid.CreateVersion7();
        var neighbour = ParcelKeys.Original(Guid.CreateVersion7());

        await PutAsync(store, neighbour, [9]);
        var original = LeaveTemp(ParcelKeys.Original(id), DateTime.UtcNow.AddHours(-3));
        var thumbnail = LeaveTemp(ParcelKeys.Thumbnail(id), DateTime.UtcNow.AddHours(-3));

        var listed = await store.ListAsync(CancellationToken).ToListAsync(CancellationToken);

        Assert.Equal(new[] { neighbour, ParcelKeys.Original(id), ParcelKeys.Thumbnail(id) }.Order(StringComparer.Ordinal),
            listed.Select(blob => blob.Name).Order(StringComparer.Ordinal));

        // Deleting the original leaves the temp of its thumbnail, which shares its prefix, and the blob beside it
        await store.DeleteAsync(ParcelKeys.Original(id), CancellationToken);

        Assert.False(File.Exists(original));
        Assert.True(File.Exists(thumbnail));
        Assert.Equal([9], await ReadAsync(store, neighbour));

        await store.DeleteAsync(ParcelKeys.Thumbnail(id), CancellationToken);

        Assert.Empty(Temps());
        Assert.Equal([neighbour], (await store.ListAsync(CancellationToken).ToListAsync(CancellationToken)).Select(blob => blob.Name));
    }

    /// <summary>
    ///     The orphan scan judges a name by its age, which for a temp is when it was last written: a running upload
    ///     keeps writing, so it keeps looking young.
    /// </summary>
    [Fact]
    public async Task A_temp_is_as_old_as_its_last_write()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());
        var lastWrite = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var temp = LeaveTemp(key, lastWrite);

        var blob = Assert.Single(await store.ListAsync(CancellationToken).ToListAsync(CancellationToken));

        Assert.Equal(key, blob.Name);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(temp), TimeSpan.Zero), blob.CreatedOn);
        Assert.Equal(new DateTimeOffset(lastWrite, TimeSpan.Zero), blob.CreatedOn);
    }

    [Fact]
    public async Task A_temp_whose_name_the_store_never_writes_is_neither_listed_nor_deleted()
    {
        var store = Store();
        var id = Guid.CreateVersion7();

        Directory.CreateDirectory(UploadingDirectory);
        var foreign = Path.Combine(UploadingDirectory, $"{id:N}.not-hex.tmp");
        await File.WriteAllBytesAsync(foreign, [1], CancellationToken);

        Assert.Empty(await store.ListAsync(CancellationToken).ToListAsync(CancellationToken));

        await store.DeleteAsync(ParcelKeys.Original(id), CancellationToken);

        Assert.True(File.Exists(foreign));
    }

    /// <summary>
    ///     A file in the root whose name is a key in upper case was not written by the store. It is listed, as a
    ///     foreign blob in Azure is, but it is no parcel's, so the orphan scan reports it instead of deleting it and
    ///     never hands the store a name it refuses: one stray file must not stop the scan of every other.
    /// </summary>
    [Fact]
    public async Task A_root_file_named_like_a_key_in_upper_case_is_listed_but_is_no_parcel_and_stays()
    {
        var store = Store();
        var orphan = ParcelKeys.Original(Guid.CreateVersion7());
        await PutAsync(store, orphan, [1]);

        var stray = Guid.CreateVersion7().ToString("N").ToUpperInvariant();
        var strayThumbnail = $"{Guid.CreateVersion7().ToString("N").ToUpperInvariant()}.thumb";
        await File.WriteAllBytesAsync(Path.Combine(_root, stray), [2], CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, strayThumbnail), [3], CancellationToken);

        var listed = (await store.ListAsync(CancellationToken).ToListAsync(CancellationToken))
            .Select(blob => blob.Name)
            .ToArray();

        Assert.Equal(new[] { orphan, stray, strayThumbnail }.Order(StringComparer.Ordinal),
            listed.Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // What the scan does with each name it lists
        foreach (var name in listed)
            if (ParcelKeys.TryParse(name, out _))
                await store.DeleteAsync(name, CancellationToken);

        Assert.Equal(new[] { stray, strayThumbnail }.Order(StringComparer.Ordinal),
            Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    ///     The sweep erases a parcel whose upload is still sending when the upload has taken too long. The upload then
    ///     ends as discarded, as it does in Azure when its staged blocks are gone, and leaves nothing behind.
    /// </summary>
    [Fact]
    public async Task A_put_whose_temp_is_deleted_mid_upload_is_discarded_and_leaves_nothing()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());
        var source = new CallbackStream(new byte[200_000], 100_000, () => store.DeleteAsync(key, CancellationToken));

        var exception = await Assert.ThrowsAsync<ParcelWriteDiscardedException>(() =>
            store.PutAsync(key, source, "application/octet-stream", CancellationToken));

        Assert.True(source.CallbackRan);
        Assert.Contains(key, exception.Message);
        Assert.False(File.Exists(Path.Combine(_root, key)));
        Assert.Empty(Temps());
        Assert.Null(await store.OpenReadAsync(key, CancellationToken));
    }

    /// <summary>
    ///     A file that appeared under the name while the bytes were being written is kept, and the put fails as one
    ///     that found it there at the start does.
    /// </summary>
    [Fact]
    public async Task A_put_never_overwrites_a_file_that_appeared_during_its_write()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());
        var source = new CallbackStream(new byte[200_000], 100_000, () =>
        {
            File.WriteAllBytes(Path.Combine(_root, key), [1, 2, 3]);

            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PutAsync(key, source, "application/octet-stream", CancellationToken));

        Assert.True(source.CallbackRan);
        Assert.Equal([1, 2, 3], await ReadAsync(store, key));
        Assert.Empty(Temps());
    }

    [Fact]
    public async Task A_put_over_an_existing_file_is_refused_with_the_bytes_intact()
    {
        var store = Store();
        var key = ParcelKeys.Thumbnail(Guid.CreateVersion7());

        await PutAsync(store, key, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PutAsync(store, key, [4, 5, 6]));

        Assert.Equal([1, 2, 3], await ReadAsync(store, key));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, key), CancellationToken));
        Assert.Empty(Temps());
    }

    /// <summary>
    ///     A source that fails is not the store failing: the error is thrown as it is, so a client that stops sending
    ///     is not answered as a storage outage, and its temp is gone.
    /// </summary>
    [Fact]
    public async Task A_source_that_fails_part_way_throws_its_own_error_and_leaves_nothing()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());
        var failure = new IOException("The client stopped sending.");
        var source = new CallbackStream(new byte[200_000], 100_000, () => throw failure);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            store.PutAsync(key, source, "application/octet-stream", CancellationToken));

        Assert.Same(failure, exception);
        Assert.False(File.Exists(Path.Combine(_root, key)));
        Assert.Empty(Temps());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(".uploading")]
    [InlineData("{0}.thumb.x")]
    [InlineData("{0}.THUMB")]
    [InlineData("{1}")]
    [InlineData("{2}")]
    [InlineData("{2}.thumb")]
    [InlineData("../{0}")]
    public async Task A_key_that_is_not_a_parcel_blob_name_is_refused(string format)
    {
        var store = Store();
        // The id in the form a key has, with dashes, and in upper case
        var id = Guid.CreateVersion7();
        var key = string.Format(format, id.ToString("N"), id.ToString("D"), id.ToString("N").ToUpperInvariant());

        await Assert.ThrowsAsync<ArgumentException>(() => PutAsync(store, key, [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(key, CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(key, CancellationToken));

        Assert.False(Directory.Exists(_root));
    }

    /// <summary>
    ///     Until the first upload there is no directory: a fresh volume, or a host started before it was mounted.
    ///     Nothing but a put creates it.
    /// </summary>
    [Fact]
    public async Task Without_its_root_the_store_reads_nothing_deletes_fine_and_lists_nothing()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());

        Assert.Null(await store.OpenReadAsync(key, CancellationToken));
        await store.DeleteAsync(key, CancellationToken);
        await store.DeleteAsync(ParcelKeys.Thumbnail(Guid.CreateVersion7()), CancellationToken);
        Assert.Empty(await store.ListAsync(CancellationToken).ToListAsync(CancellationToken));

        Assert.False(Directory.Exists(_root));
    }

    /// <summary>
    ///     On Linux the files and the directory of temps are the owner's alone; Windows has no such modes.
    /// </summary>
    [Fact]
    public async Task What_is_stored_is_readable_by_its_owner_alone_on_linux()
    {
        var store = Store();
        var key = ParcelKeys.Original(Guid.CreateVersion7());

        await PutAsync(store, key, [1]);

        Assert.True(File.Exists(Path.Combine(_root, key)));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, key)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(UploadingDirectory));
        }
    }

    /// <summary>
    ///     Hands out <paramref name="bytes" /> and runs <paramref name="callback" /> once, after the first
    ///     <paramref name="callbackAfter" /> bytes are read, as a slow upload that something happens to part way.
    /// </summary>
    private sealed class CallbackStream(byte[] bytes, int callbackAfter, Func<Task> callback) : Stream
    {
        private int _position;

        public bool CallbackRan { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= callbackAfter && !CallbackRan)
            {
                CallbackRan = true;
                await callback();
            }

            var count = Math.Min(buffer.Length, Math.Min(bytes.Length - _position, 16_384));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
