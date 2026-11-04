namespace Atom.Targets;

internal interface IBuildWebApp : IChromeExtensionHelper, IConfigurationHelper
{
    const string WebAppArtifactName = "WebApp";

    RootedPath WebAppDirectory => WebWorkspaceDirectory / "web";

    /// <summary>
    ///     The static site for any static host: the landing, privacy and terms pages and the app under <c>app/</c>.
    ///     Hosting it is not part of the build yet; see docs/components/web-app.md.
    /// </summary>
    Target BuildWebApp =>
        t => t
            .DescribedAs("Builds the web app's static site: the landing, privacy and terms pages and the app")
            .RequiresParam(nameof(ApiBaseUrlProduction))
            .UsesParam(nameof(ServerClientId))
            .ProducesArtifact(WebAppArtifactName)
            .Executes(async cancellationToken =>
            {
                var publishDirectory = RootedFileSystem.AtomPublishDirectory / WebAppArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(publishDirectory);

                // build.mjs prefers these over any local .env file. The web app's API_URL is the packets endpoint
                // itself, not the API root, and it signs in with the web client id, the API's main audience
                Dictionary<string, string?> buildVariables = new()
                {
                    ["API_URL"] = $"{ApiBaseUrlProduction!.TrimEnd('/')}/api/packets",
                };

                if (ServerClientId?.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) is true)
                    buildVariables["GOOGLE_CLIENT_ID"] = ServerClientId;
                else
                    // Guard against compiling in a placeholder that would silently break Google sign-in
                    Logger.LogWarning(
                        "{Param} is not a Google OAuth client id ('{Value}'); GOOGLE_CLIENT_ID must come from src/web/.env",
                        nameof(ServerClientId),
                        ServerClientId);

                var npmPath = await RestoreChromeExtensionPackages(cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "run build")
                    {
                        WorkingDirectory = WebAppDirectory,
                        EnvironmentVariables = buildVariables,
                    },
                    cancellationToken);

                // Only the built site is published, never sources, node_modules or .env files
                CopyDirectory(WebAppDirectory / "dist", publishDirectory);
            });

    private void CopyDirectory(RootedPath source, RootedPath destination)
    {
        RootedFileSystem.Directory.CreateDirectory(destination);

        foreach (var file in RootedFileSystem.Directory.GetFiles(source))
            RootedFileSystem.File.Copy(file, destination / RootedFileSystem.Path.GetFileName(file), true);

        foreach (var directory in RootedFileSystem.Directory.GetDirectories(source))
            CopyDirectory(source / RootedFileSystem.Path.GetFileName(directory), destination / RootedFileSystem.Path.GetFileName(directory));
    }
}
