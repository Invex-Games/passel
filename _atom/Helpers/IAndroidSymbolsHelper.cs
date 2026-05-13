namespace Atom.Helpers;

internal interface IAndroidSymbolsHelper : IBuildAccessor
{
    void AddSymbolsToPublishFolder(
        RootedPath dartSymbolsPublishDirectory,
        RootedPath debugSymbolsPath,
        RootedPath reTraceMappingPublishDirectory,
        RootedPath reTraceMappingPath)
    {
        // Mobile App Debug symbols ".symbols"
        var files = AtomFileSystem.Directory.GetFiles(debugSymbolsPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var fileInfo = AtomFileSystem.FileInfo.New(file);

            AtomFileSystem.File.Copy(fileInfo.FullName, dartSymbolsPublishDirectory / $"{fileInfo.Name}");
        }

        // Zip Dart Symbols
        ZipFile.CreateFromDirectory(dartSymbolsPublishDirectory, dartSymbolsPublishDirectory + ".zip");

        AtomFileSystem.Directory.Delete(dartSymbolsPublishDirectory, true);

        // Retrace mapping.txt
        var reTraceFiles =
            AtomFileSystem.Directory.GetFiles(reTraceMappingPath, "mapping.txt", SearchOption.AllDirectories);

        foreach (var reTraceFile in reTraceFiles)
        {
            var fileInfo = AtomFileSystem.FileInfo.New(reTraceFile);

            AtomFileSystem.File.Copy(fileInfo.FullName, reTraceMappingPublishDirectory / $"{fileInfo.Name}");
        }
    }
}
