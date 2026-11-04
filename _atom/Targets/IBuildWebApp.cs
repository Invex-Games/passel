using Invex.Atom.Build.Args;

namespace Atom.Targets;

internal interface IBuildWebApp : IChromeExtensionHelper, IConfigurationHelper
{
    const string WebAppArtifactName = "WebApp";

    RootedPath WebAppDirectory => WebWorkspaceDirectory / "web";

    /// <summary>
    ///     The static site for any static host: the landing, privacy and terms pages and the app under <c>app/</c>.
    ///     The API's address and the Google web client id are compiled in, so a build serves one environment only.
    ///     PublishImages puts the site in the web image; see docs/components/web-app.md.
    /// </summary>
    Target BuildWebApp =>
        t => t
            .DescribedAs("Builds the web app's static site: the landing, privacy and terms pages and the app")
            .RequiresParam(nameof(ApiBaseUrlProduction))
            .UsesParam(nameof(ServerClientId))
            .ProducesArtifact(WebAppArtifactName)
            .Executes(async cancellationToken =>
            {
                // The API's origin goes into the app and into its Content-Security-Policy, so it has to be exactly
                // an origin: a path would end up in the app's request URLs and in the policy
                if (!Uri.TryCreate(ApiBaseUrlProduction, UriKind.Absolute, out var apiBaseUrl) ||
                    apiBaseUrl.Scheme != Uri.UriSchemeHttps ||
                    apiBaseUrl.AbsolutePath != "/" ||
                    apiBaseUrl.Query.Length > 0 ||
                    apiBaseUrl.Fragment.Length > 0 ||
                    apiBaseUrl.UserInfo.Length > 0)
                    throw new StepFailedException($"{nameof(ApiBaseUrlProduction)} must be an https origin with no " +
                                                  $"path, such as https://api.passel.link ('{ApiBaseUrlProduction}')");

                // build.mjs prefers these over any local .env file. The web app's API_URL is the packets endpoint
                // itself, not the API root, and it signs in with the web client id, the API's main audience
                Dictionary<string, string?> buildVariables = new()
                {
                    ["API_URL"] = $"{ApiBaseUrlProduction!.TrimEnd('/')}/api/packets",
                };

                if (ServerClientId?.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) is true)
                    buildVariables["GOOGLE_CLIENT_ID"] = ServerClientId;
                // A headless build (CI) makes the site that ships, and must never fall back to a developer's .env
                else if (GetService<CommandLineArgs>().HasHeadless)
                    throw new StepFailedException($"{nameof(ServerClientId)} is not a Google OAuth client id " +
                                                  $"('{ServerClientId}'); set the GOOGLE_WEB_CLIENT_ID variable");
                else
                    // Guard against compiling in a placeholder that would silently break Google sign-in
                    Logger.LogWarning(
                        "{Param} is not a Google OAuth client id ('{Value}'); GOOGLE_CLIENT_ID must come from src/web/.env",
                        nameof(ServerClientId),
                        ServerClientId);

                var publishDirectory = RootedFileSystem.AtomPublishDirectory / WebAppArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(publishDirectory);

                var npmPath = await RestoreChromeExtensionPackages(cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "run build")
                    {
                        WorkingDirectory = WebAppDirectory,
                        EnvironmentVariables = buildVariables,
                    },
                    cancellationToken);

                // Only the built site is published, never sources, node_modules or .env files
                RootedFileSystem.CopyDirectory(WebAppDirectory / "dist", publishDirectory);
            });
}
