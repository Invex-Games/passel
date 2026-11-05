using Invex.Atom.Build.Reports;

namespace Atom.Targets;

/// <summary>
///     Builds the two images of the self-hosted deployment (ADR 0004) and, on main, pushes them to the container
///     registry: the API, which also runs the database migration with the <c>db-migrate</c> command, and the web app,
///     the static site served by Caddy. Each image is tagged with the build's version and its commit
///     (<see cref="IImageTagHelper" />), and pushed by the rules of <see cref="IContainerRegistryHelper" />.
/// </summary>
internal interface IPublishImages : IBuildWebApp, ITestTargets, ISetupBuildInfo, IContainerRegistryHelper,
    IReportsHelper
{
    private RootedPath WebImageDirectory => RootedFileSystem.AtomRootDirectory / "deploy" / "web";

    Target PublishImages =>
        t => t
            .DescribedAs("Builds the API and web app images and, on main, pushes them to the container registry")
            .DependsOn(nameof(TestWebApi))
            .UsesParam(nameof(ContainerRegistry))
            .UsesParam(nameof(ContainerNamespace))
            .UsesParam(nameof(PushImages))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildVersion))
            .ConsumesArtifact(nameof(BuildWebApp), WebAppArtifactName)
            .Executes(async cancellationToken =>
            {
                var push = PushesImages;
                var version = ImageTag;
                var revision = SourceRevision;
                string[] tags = [version, ShaTag];

                // Without a registry in the name the SDK loads the image into the local daemon instead of pushing it
                var repositoryPrefix = push
                    ? $"{ContainerRegistry}/{ContainerNamespace}"
                    : ContainerNamespace;

                var webApiRepository = $"{repositoryPrefix}/{WebApiImageName}";
                var webRepository = $"{repositoryPrefix}/{WebImageName}";

                var pushWebApi = push && await CheckTagsAreFree(webApiRepository, tags, revision, cancellationToken);

                await PublishWebApiImage(pushWebApi, tags, revision, cancellationToken);

                await BuildWebImage(webRepository, tags, version, revision, cancellationToken);

                var pushWeb = push && await CheckTagsAreFree(webRepository, tags, revision, cancellationToken);

                if (pushWeb)
                    foreach (var tag in tags)
                        await ProcessRunner.RunAsync(
                            new("docker", CommandLine.Join(["push", $"{webRepository}:{tag}"])),
                            cancellationToken);

                var outcome = push
                    ? "pushed"
                    : "built locally, not pushed";

                string Outcome(bool pushed) =>
                    push && !pushed
                        ? "already pushed"
                        : outcome;

                AddReportData(new ListReportData([
                    .. tags.Select(tag => $"{webApiRepository}:{tag} ({Outcome(pushWebApi)})"),
                    .. tags.Select(tag => $"{webRepository}:{tag} ({Outcome(pushWeb)})"),
                ])
                {
                    Title = $"Images {version}",
                });

                Logger.LogInformation("Images {Version} ({Revision}) {Outcome}", version, revision, outcome);
            });

    /// <summary>
    ///     Publishes the API as a container image with the .NET SDK's container support, on its default base image
    ///     (ASP.NET on Debian, user <c>app</c>, port 8080). The SDK labels the image with its source, revision and
    ///     version.
    /// </summary>
    private async Task PublishWebApiImage(
        bool push,
        string[] tags,
        string revision,
        CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "publish",
            Projects.Invex_Passel_WebApi.Path(RootedFileSystem).ToString(),
            "-c",
            "Release",
            "-r",
            "linux-x64",
            "-t:PublishContainer",
            $"-p:ContainerRepository={ContainerNamespace}/{WebApiImageName}",
            // The version label, and the version the API reports, are the build's own rather than the SDK's 1.0.0
            $"-p:Version={BuildVersion}",
            $"-p:SourceRevisionId={revision}",
        ];

        if (push)
            arguments = [.. arguments, $"-p:ContainerRegistry={ContainerRegistry}"];

        await ProcessRunner.RunAsync(new("dotnet", CommandLine.Join(arguments))
            {
                // MSBuild reads environment variables as properties without splitting them on ';', which a
                // command-line property would need quoting for
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["ContainerImageTags"] = string.Join(';', tags),
                },
                OutputLogLevel = LogLevel.Information,
            },
            cancellationToken);
    }

    /// <summary>
    ///     Builds the web image from <c>deploy/web</c> and the site that BuildWebApp built, staged together as the
    ///     build context so that nothing else in the repository can reach the image. The image holds no environment's
    ///     values, so the same image is promoted from one environment to the next.
    /// </summary>
    private async Task BuildWebImage(
        string repository,
        string[] tags,
        string version,
        string revision,
        CancellationToken cancellationToken)
    {
        var site = RootedFileSystem.AtomArtifactsDirectory / WebAppArtifactName;

        // A local run without a workflow's artifact download uses BuildWebApp's own output
        if (!RootedFileSystem.Directory.Exists(site))
            site = RootedFileSystem.AtomPublishDirectory / WebAppArtifactName;

        if (!RootedFileSystem.File.Exists(site / "index.html"))
            throw new StepFailedException($"No built web app at {site}; run BuildWebApp first");

        Logger.LogInformation("Building the web image from the site in {Site}", site);

        var context = RootedFileSystem.AtomTempDirectory / "web-image";
        RootedFileSystem.CleanOrCreateDirectory(context);

        RootedFileSystem.File.Copy(WebImageDirectory / "Dockerfile", context / "Dockerfile", true);
        RootedFileSystem.File.Copy(WebImageDirectory / "Caddyfile", context / "Caddyfile", true);
        RootedFileSystem.File.Copy(WebImageDirectory / "entrypoint.sh", context / "entrypoint.sh", true);
        RootedFileSystem.CopyDirectory(site, context / "site");

        await ProcessRunner.RunAsync(new("docker",
            CommandLine.Join([
                "build",
                // A fresh base image on every build brings Caddy's and Alpine's patches in
                "--pull",
                .. tags.SelectMany(tag => new[] { "-t", $"{repository}:{tag}" }),
                "--label",
                $"org.opencontainers.image.revision={revision}",
                "--label",
                $"org.opencontainers.image.version={version}",
                context.ToString(),
            ]))
            {
                OutputLogLevel = LogLevel.Information,
                // BuildKit writes its progress to stderr, which is not a warning
                ErrorLogLevel = LogLevel.Information,
            },
            cancellationToken);
    }
}
