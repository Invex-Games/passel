namespace Atom.Util;

[PublicAPI]
public static class RootedFileSystemExtensions
{
    extension(IRootedFileSystem rootedFileSystem)
    {
        [PublicAPI]
        public void CleanOrCreateDirectory(RootedPath directories)
        {
            if (rootedFileSystem.Directory.Exists(directories))
                rootedFileSystem.Directory.Delete(directories, true);

            rootedFileSystem.Directory.CreateDirectory(directories);
        }

        [PublicAPI]
        public void CleanOrCreateDirectories(IEnumerable<RootedPath> directories)
        {
            foreach (var path in directories)
            {
                if (rootedFileSystem.Directory.Exists(path))
                    rootedFileSystem.Directory.Delete(path, true);

                rootedFileSystem.Directory.CreateDirectory(path);
            }
        }
    }
}
