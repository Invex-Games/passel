namespace Atom.Helpers;

internal interface IChromeExtensionHelper : IProcessHelper
{
    /// <summary>
    ///     The npm workspace holding the TypeScript code: the shared packages, the Chrome extension and, later,
    ///     the web app. Packages are installed here, once, for every workspace member.
    /// </summary>
    RootedPath WebWorkspaceDirectory => RootedFileSystem.AtomRootDirectory / "src";

    RootedPath ChromeExtensionDirectory => WebWorkspaceDirectory / "chrome-extension";

    /// <summary>
    ///     Installs the workspace's locked npm packages.
    /// </summary>
    /// <returns>The path to npm, for running the workspace's scripts afterwards.</returns>
    async Task<RootedPath> RestoreChromeExtensionPackages(CancellationToken cancellationToken = default)
    {
        var npmPath = await FindEnvironmentPath("npm", cancellationToken);

        await ProcessRunner.RunAsync(new(npmPath, "ci")
            {
                WorkingDirectory = WebWorkspaceDirectory,
                OutputLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        return npmPath;
    }
}
