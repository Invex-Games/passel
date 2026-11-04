using System.Runtime.InteropServices;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Flushes a directory to disk on Linux, which .NET has no API for. A file renamed into a directory is only
///     durable once the directory itself is synced: without it, ext4 can lose the rename after a power cut even
///     though the file's bytes were flushed. Does nothing on other systems, which are only ever development machines.
/// </summary>
internal static partial class PosixDirectory
{
    // O_RDONLY | O_CLOEXEC, the same on x64 and arm64. O_DIRECTORY is left out because its value differs between the
    // two; a read-only descriptor of a directory can be synced all the same
    private const int OpenFlags = 0x80000;

    /// <summary>
    ///     Syncs <paramref name="path" /> to disk on Linux, throwing an <see cref="IOException" /> carrying the errno
    ///     when it cannot be opened or synced.
    /// </summary>
    public static void Fsync(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var descriptor = Open(path, OpenFlags);

        if (descriptor < 0)
            throw Failure("open");

        try
        {
            if (FSync(descriptor) != 0)
                throw Failure("fsync");
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static IOException Failure(string call)
    {
        var errno = Marshal.GetLastPInvokeError();

        return new($"The {call} of a directory to sync it failed with errno {errno}.", errno);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);
}
