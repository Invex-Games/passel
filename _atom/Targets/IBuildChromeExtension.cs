namespace Atom.Targets;

internal interface IBuildChromeExtension : IProcessHelper, IConfigurationHelper
{
    const string ChromeExtensionArtifactName = "ChromeExtension";

    [ParamDefinition("chrome-extension-client-id", "The Google OAuth client id used by the Chrome Extension")]
    string? ChromeExtensionClientId => GetParam(() => ChromeExtensionClientId);

    Target BuildChromeExtension =>
        t => t
            .DescribedAs("Builds and packages the Chrome Extension")
            .RequiresParam(nameof(ApiBaseUrlProduction))
            .UsesParam(nameof(ChromeExtensionClientId))
            .ProducesArtifact(ChromeExtensionArtifactName)
            .Executes(async cancellationToken =>
            {
                var chromeExtensionDirectory = RootedFileSystem.AtomRootDirectory / "src" / "chrome-extension";
                var chromeExtensionBuildDirectory = chromeExtensionDirectory / "dist";
                var chromeExtensionPublishDirectory = RootedFileSystem.AtomPublishDirectory / ChromeExtensionArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(chromeExtensionPublishDirectory);

                // build.mjs prefers these over any local .env file; without a client id param it falls back to .env.
                // The extension's API_URL is the packets endpoint itself, not the API root.
                Dictionary<string, string?> buildVariables = new()
                {
                    ["API_URL"] = $"{ApiBaseUrlProduction!.TrimEnd('/')}/api/packets",
                };

                if (ChromeExtensionClientId is { Length: > 0 })
                    buildVariables["GOOGLE_CLIENT_ID"] = ChromeExtensionClientId;

                var npmPath = await FindEnvironmentPath("npm", cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "ci")
                    {
                        WorkingDirectory = chromeExtensionDirectory,
                        OutputLogLevel = LogLevel.Debug,
                    },
                    cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "run build")
                    {
                        WorkingDirectory = chromeExtensionDirectory,
                        EnvironmentVariables = buildVariables,
                    },
                    cancellationToken);

                // Only the self-contained build output is packaged, never sources, node_modules or .env files
                await ZipFile.CreateFromDirectoryAsync(chromeExtensionBuildDirectory,
                    chromeExtensionPublishDirectory / "chrome-extension.zip",
                    CompressionLevel.Optimal,
                    false,
                    cancellationToken);
            });
}
