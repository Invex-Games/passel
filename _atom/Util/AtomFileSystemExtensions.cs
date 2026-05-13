namespace Atom.Util;

[PublicAPI]
public static class AtomFileSystemExtensions
{
    extension(IAtomFileSystem atomFileSystem)
    {
        [PublicAPI]
        public void CleanOrCreateDirectory(RootedPath directories)
        {
            if (atomFileSystem.Directory.Exists(directories))
                atomFileSystem.Directory.Delete(directories, true);

            atomFileSystem.Directory.CreateDirectory(directories);
        }

        [PublicAPI]
        public void CleanOrCreateDirectories(IEnumerable<RootedPath> directories)
        {
            foreach (var path in directories)
            {
                if (atomFileSystem.Directory.Exists(path))
                    atomFileSystem.Directory.Delete(path, true);

                atomFileSystem.Directory.CreateDirectory(path);
            }
        }
    }
}
