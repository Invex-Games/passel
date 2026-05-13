namespace Atom.Targets;

internal interface IBuildChromeExtension : IBuildAccessor
{
    const string ChromeExtensionArtifactName = "ChromeExtension";

    Target BuildChromeExtension =>
        t => t
            .DescribedAs("Packages the Chrome Extension")
            .ProducesArtifact(ChromeExtensionArtifactName)
            .Executes(async () =>
            {
                var chromeExtensionDirectory = AtomFileSystem.AtomRootDirectory / "src" / "chrome-extension";
                var chromeExtensionPublishDirectory = AtomFileSystem.AtomPublishDirectory / ChromeExtensionArtifactName;

                if (AtomFileSystem.Directory.Exists(chromeExtensionPublishDirectory))
                    AtomFileSystem.Directory.Delete(chromeExtensionPublishDirectory, true);

                AtomFileSystem.Directory.CreateDirectory(chromeExtensionPublishDirectory);

                var chromeExtensionZipPath = chromeExtensionPublishDirectory / "chrome-extension.zip";

                await ZipFile.CreateFromDirectoryAsync(chromeExtensionDirectory,
                    chromeExtensionZipPath,
                    CompressionLevel.Optimal,
                    false);
            });
}
