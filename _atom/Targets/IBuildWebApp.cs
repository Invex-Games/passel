namespace Atom.Targets;

internal interface IBuildWebApp : IChromeExtensionHelper
{
    const string WebAppArtifactName = "WebApp";

    RootedPath WebAppDirectory => WebWorkspaceDirectory / "web";

    /// <summary>
    ///     The static site: the landing, privacy and terms pages and the app under <c>app/</c>. It holds no
    ///     environment's values: the app reads the API's address and the Google web client id from
    ///     <c>/app/config.json</c> when it starts, and the security policies are response headers, so one build
    ///     serves every environment. PublishImages puts the site in the web image, which provides both when its
    ///     container starts; see docs/components/web-app.md.
    /// </summary>
    Target BuildWebApp =>
        t => t
            .DescribedAs("Builds the web app's static site: the landing, privacy and terms pages and the app")
            .ProducesArtifact(WebAppArtifactName)
            .Executes(async cancellationToken =>
            {
                var publishDirectory = RootedFileSystem.AtomPublishDirectory / WebAppArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(publishDirectory);

                var npmPath = await RestoreChromeExtensionPackages(cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "run build")
                    {
                        WorkingDirectory = WebAppDirectory,
                    },
                    cancellationToken);

                // Only the built site is published, never sources, node_modules or .env files
                RootedFileSystem.CopyDirectory(WebAppDirectory / "dist", publishDirectory);
            });
}
