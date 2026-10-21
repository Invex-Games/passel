namespace Atom.Helpers;

internal interface IChromeExtensionHelper : IProcessHelper
{
    RootedPath ChromeExtensionDirectory => RootedFileSystem.AtomRootDirectory / "src" / "chrome-extension";

    /// <summary>
    ///     Installs the extension's locked npm packages.
    /// </summary>
    /// <returns>The path to npm, for running the extension's scripts afterwards.</returns>
    async Task<RootedPath> RestoreChromeExtensionPackages(CancellationToken cancellationToken = default)
    {
        var npmPath = await FindEnvironmentPath("npm", cancellationToken);

        await ProcessRunner.RunAsync(new(npmPath, "ci")
            {
                WorkingDirectory = ChromeExtensionDirectory,
                OutputLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        return npmPath;
    }
}
