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
        var files = RootedFileSystem.Directory.GetFiles(debugSymbolsPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var fileInfo = RootedFileSystem.FileInfo.New(file);

            RootedFileSystem.File.Copy(fileInfo.FullName, dartSymbolsPublishDirectory / $"{fileInfo.Name}");
        }

        // Zip Dart Symbols
        ZipFile.CreateFromDirectory(dartSymbolsPublishDirectory, dartSymbolsPublishDirectory + ".zip");

        RootedFileSystem.Directory.Delete(dartSymbolsPublishDirectory, true);

        // Retrace mapping.txt
        var reTraceFiles =
            RootedFileSystem.Directory.GetFiles(reTraceMappingPath, "mapping.txt", SearchOption.AllDirectories);

        foreach (var reTraceFile in reTraceFiles)
        {
            var fileInfo = RootedFileSystem.FileInfo.New(reTraceFile);

            RootedFileSystem.File.Copy(fileInfo.FullName, reTraceMappingPublishDirectory / $"{fileInfo.Name}");
        }
    }
}
