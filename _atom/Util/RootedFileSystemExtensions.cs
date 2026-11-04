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

        /// <summary>
        ///     Copies a directory and everything under it, overwriting files that are already at the destination.
        /// </summary>
        [PublicAPI]
        public void CopyDirectory(RootedPath source, RootedPath destination)
        {
            rootedFileSystem.Directory.CreateDirectory(destination);

            foreach (var file in rootedFileSystem.Directory.GetFiles(source))
                rootedFileSystem.File.Copy(file, destination / rootedFileSystem.Path.GetFileName(file), true);

            foreach (var directory in rootedFileSystem.Directory.GetDirectories(source))
            {
                var name = rootedFileSystem.Path.GetFileName(directory);
                rootedFileSystem.CopyDirectory(source / name, destination / name);
            }
        }
    }
}
