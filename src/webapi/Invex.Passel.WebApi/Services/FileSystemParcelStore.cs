using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Keeps the bytes of uploaded files as files in one directory on the local disk,
///     <see cref="ParcelOptions.FileSystemRoot" />, for a self-hosted deployment with a volume of its own (ADR 0004). A
///     finished blob is the file <c>{Root}/{key}</c>; an upload in flight writes <c>{Root}/.uploading/{key}.{random}.tmp</c>
///     and renames it into place once its bytes are on disk, so a blob is there whole or not at all. Nothing touches the
///     disk until the store is used: the <c>db-migrate</c> job builds the host without the volume. The disk must be
///     local; a network share weakens the rename that refuses to overwrite and the syncs that make a write durable.
/// </summary>
internal sealed partial class FileSystemParcelStore(
    IOptions<ParcelOptions> options,
    ILogger<FileSystemParcelStore> logger) : IParcelStore
{
    private const string UploadingDirectoryName = ".uploading";
    private const string TempSuffix = ".tmp";

    // What a download reads from the disk at a time, as the Azure store reads from storage
    private const int ReadBufferSize = 1024 * 1024;

    // What an upload writes to the disk at a time, as Stream.CopyToAsync would
    private const int CopyBufferSize = 81_920;

    // A random 32-digit hex part keeps two uploads of one name, which never happen, from sharing a temp
    private const int RandomPartLength = 32;

    private string Root =>
        options.Value.FileSystemRoot ??
        throw new InvalidOperationException($"{nameof(ParcelOptions.FileSystemRoot)} is not set.");

    private string UploadingDirectory =>
        Path.Combine(Root, UploadingDirectoryName);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        RequireKey(key);

        var final = Path.Combine(Root, key);

        // The rename below refuses to overwrite too; this only spares the bytes of an upload bound to fail
        if (File.Exists(final))
            throw Exists(key, null);

        var temp = await WriteTempAsync(key, content, cancellationToken);

        try
        {
            File.Move(temp, final, overwrite: false);
        }
        catch (IOException exception) when (!File.Exists(temp))
        {
            // DeleteAsync removed the temp while the bytes were still arriving: the sweep erased this parcel
            // mid-upload, as discarding its staged blocks does in Azure
            throw new ParcelWriteDiscardedException(key, exception);
        }
        catch (IOException exception) when (File.Exists(final))
        {
            DeleteTemp(key, temp);

            throw Exists(key, exception);
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            DeleteTemp(key, temp);

            throw new ParcelStoreFailedException("write", key, exception);
        }

        try
        {
            // The parcel is recorded as uploaded as soon as this returns, so the rename must survive a power cut
            PosixDirectory.Fsync(Root);
        }
        catch (IOException exception)
        {
            throw new ParcelStoreFailedException("write", key, exception);
        }
    }

    public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        RequireKey(key);

        try
        {
            // Opened without looking first, which would race a delete anyway. Deletes are shared, so the sweep can
            // erase a file while it downloads: the download keeps the bytes it has open
            var stream = new FileStream(Path.Combine(Root, key),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return Task.FromResult<ParcelContent?>(new(stream, stream.Length));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // No such file, or nothing stored yet at all
            return Task.FromResult<ParcelContent?>(null);
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("read", key, exception);
        }
    }

    /// <summary>
    ///     Deletes the temps an upload under the name left, then the file. An upload still writing its temp then fails
    ///     its rename and is discarded; one that renamed in between loses its file to the second step, and the parcel,
    ///     already deleted, refuses to complete. Only the temps of this name go: the original and the thumbnail of a
    ///     parcel share their prefix.
    /// </summary>
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var id = RequireKey(key);

        try
        {
            foreach (var temp in Directory.EnumerateFiles(UploadingDirectory, $"{id:N}*"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TempKey(Path.GetFileName(temp)) == key)
                    File.Delete(temp);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // No upload has ever started
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("delete", key, exception);
        }

        try
        {
            File.Delete(Path.Combine(Root, key));
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing stored yet at all
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("delete", key, exception);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     The files in the root, then the names of uploads still in their temps, once each. Each is as old as its last
    ///     write: a blob is never written again, and a temp still being written keeps looking young, so the orphan
    ///     scan leaves it alone while it runs. A temp whose name the store never writes is left out, as the
    ///     <c>.uploading</c> directory itself is. Any other file in the root is listed as it is, as a foreign blob is
    ///     in Azure: the scan deletes only names <see cref="ParcelKeys.TryParse" /> accepts, which this store accepts
    ///     as keys too, and reports the rest.
    /// </summary>
    public async IAsyncEnumerable<StoredBlob> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, lastWrite) in Files(Root, cancellationToken))
            if (listed.Add(name))
                yield return new(name, lastWrite);

        // Several temps under one name keep the youngest, so a running upload is never taken for an old one
        var uploading = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var (name, lastWrite) in Files(UploadingDirectory, cancellationToken))
            if (TempKey(name) is { } key && !listed.Contains(key))
                uploading[key] = uploading.TryGetValue(key, out var seen) && seen > lastWrite
                    ? seen
                    : lastWrite;

        foreach (var (key, lastWrite) in uploading)
            yield return new(key, lastWrite);
    }

    /// <summary>
    ///     Writes the bytes to a temp of their own and flushes them to disk, returning its path. The temp is deleted
    ///     again when the write fails. A failure of the disk is a <see cref="ParcelStoreFailedException" />; one of
    ///     <paramref name="content" /> is thrown as it is, since it is not the store that failed.
    /// </summary>
    private async Task<string> WriteTempAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var directory = UploadingDirectory;
        var temp = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}{TempSuffix}");
        FileStream stream;

        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,

                // So the sweep can delete it mid-write on Windows as well; on Linux a delete always can
                Share = FileShare.Delete,
                Options = FileOptions.Asynchronous,
            };

            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            stream = new(temp, streamOptions);
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("write", key, exception);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                    break;

                try
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                catch (Exception exception) when (IsDiskFailure(exception))
                {
                    throw new ParcelStoreFailedException("write", key, exception);
                }
            }

            try
            {
                stream.Flush(flushToDisk: true);
                await stream.DisposeAsync();
            }
            catch (Exception exception) when (IsDiskFailure(exception))
            {
                throw new ParcelStoreFailedException("write", key, exception);
            }
        }
        catch
        {
            // Closed first, so nothing holds it open; a failure closing it is the failure already being thrown
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception exception) when (IsDiskFailure(exception))
            {
            }

            DeleteTemp(key, temp);

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return temp;
    }

    /// <summary>
    ///     Deletes a temp of a failed write, once and at best: what is left is deleted with the parcel, or by the
    ///     orphan scan.
    /// </summary>
    private void DeleteTemp(string key, string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            LogTempNotDeleted(key, exception.GetType().Name, exception.HResult);
        }
    }

    /// <summary>
    ///     The files directly in <paramref name="directory" />, with when each was last written; nothing when the
    ///     directory is not there.
    /// </summary>
    private static IEnumerable<(string Name, DateTimeOffset LastWrite)> Files(
        string directory,
        CancellationToken cancellationToken)
    {
        IEnumerator<(string, DateTimeOffset)> files;

        try
        {
            files = new FileSystemEnumerable<(string, DateTimeOffset)>(directory,
                (ref entry) => (entry.FileName.ToString(), entry.LastWriteTimeUtc),
                new()
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.None,
                })
            {
                ShouldIncludePredicate = (ref entry) => !entry.IsDirectory,
            }.GetEnumerator();
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("list", string.Empty, exception);
        }

        using (files)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MoveNext(files))
                    yield break;

                yield return files.Current;
            }
        }
    }

    private static bool MoveNext(IEnumerator<(string, DateTimeOffset)> files)
    {
        try
        {
            return files.MoveNext();
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while it was being read, so there is nothing more in it
            return false;
        }
        catch (Exception exception) when (IsDiskFailure(exception))
        {
            throw new ParcelStoreFailedException("list", string.Empty, exception);
        }
    }

    /// <summary>
    ///     The key a temp was written for, from its name <c>{key}.{random}.tmp</c>, or <c>null</c> for a name the
    ///     store never writes.
    /// </summary>
    internal static string? TempKey(string fileName)
    {
        if (!fileName.EndsWith(TempSuffix, StringComparison.Ordinal))
            return null;

        var withoutSuffix = fileName[..^TempSuffix.Length];
        var dot = withoutSuffix.Length - RandomPartLength - 1;

        if (dot <= 0 || withoutSuffix[dot] != '.' || !IsHex(withoutSuffix.AsSpan(dot + 1)))
            return null;

        var key = withoutSuffix[..dot];

        return IsKey(key)
            ? key
            : null;
    }

    private static bool IsHex(ReadOnlySpan<char> value) =>
        value.Length == RandomPartLength && !value.ContainsAnyExcept("0123456789abcdef");

    /// <summary>
    ///     Whether <paramref name="key" /> is one of the two names a parcel's blobs have. Nothing else may reach a
    ///     path, so no key can lead out of the root.
    /// </summary>
    private static bool IsKey(string key) =>
        IsKey(key, out _);

    private static bool IsKey(string key, out Guid id) =>
        ParcelKeys.TryParse(key, out id);

    /// <summary>
    ///     The parcel <paramref name="key" /> names, throwing an <see cref="ArgumentException" /> for any other name.
    /// </summary>
    private static Guid RequireKey(string key) =>
        IsKey(key, out var id)
            ? id
            : throw new ArgumentException($"{key} is not the name of a parcel's blob.", nameof(key));

    /// <summary>
    ///     Whether <paramref name="exception" /> is the disk refusing or failing: full, not writable, gone bad.
    /// </summary>
    private static bool IsDiskFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static InvalidOperationException Exists(string key, Exception? innerException) =>
        new($"A blob named {key} exists already; parcels are never overwritten.", innerException);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The temp of a failed write of {Key} could not be deleted ({ExceptionType}, {HResult}); the sweep deletes it with its parcel")]
    private partial void LogTempNotDeleted(string key, string exceptionType, int hResult);
}
