using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class PosixDirectoryTests
{
    [Fact]
    public void A_directory_that_is_there_syncs()
    {
        var directory = Directory.CreateTempSubdirectory("passel-sync-");

        try
        {
            PosixDirectory.Fsync(directory.FullName);
        }
        finally
        {
            directory.Delete();
        }
    }

    /// <summary>
    ///     On Linux the libc calls are made, and a failure is an I/O error carrying the errno (ENOENT); elsewhere
    ///     nothing is called at all.
    /// </summary>
    [Fact]
    public void A_directory_that_is_not_there_fails_on_linux_only()
    {
        var missing = Path.Combine(Path.GetTempPath(), "passel-sync-missing", Guid.NewGuid().ToString("N"));

        if (OperatingSystem.IsLinux())
        {
            var exception = Assert.Throws<IOException>(() => PosixDirectory.Fsync(missing));

            Assert.Equal(2, exception.HResult);
        }
        else
        {
            PosixDirectory.Fsync(missing);
        }
    }
}
