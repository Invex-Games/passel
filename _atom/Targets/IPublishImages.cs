using System.Text.Json;
using Invex.Atom.Build.Reports;

namespace Atom.Targets;

/// <summary>
///     Builds the two images of the self-hosted deployment (ADR 0004) and, on main, pushes them to the container
///     registry: the API, which also runs the database migration with the <c>db-migrate</c> command, and the web app,
///     the static site served by Caddy. Each image is tagged with the build's version and its commit
///     (<see cref="IImageTagHelper" />). A version tag that is already in the registry is never moved: the same
///     commit's push is skipped and another commit's fails.
/// </summary>
internal interface IPublishImages : IBuildWebApp, ITestTargets, ISetupBuildInfo, IImageTagHelper, IReportsHelper
{
    const string WebApiImageName = "passel-webapi";

    const string WebImageName = "passel-web";

    [ParamDefinition("container-registry", "The registry the images are pushed to, such as ghcr.io")]
    string ContainerRegistry => GetParam(() => ContainerRegistry, "ghcr.io");

    [ParamDefinition("container-namespace", "The namespace of the images in the registry, such as invex-games")]
    string ContainerNamespace => GetParam(() => ContainerNamespace, "invex-games");

    [ParamDefinition("push-images",
        "Whether to push the images to the registry; only a build of refs/heads/main ever pushes")]
    bool PushImages => GetParam(() => PushImages, false, value => bool.TryParse(value, out var push) && push);

    /// <summary>
    ///     Images are pushed from main only, so a tag in the registry always names a commit that went through main
    ///     and its tests. Anywhere else, and locally, they are only built, into the local Docker daemon.
    /// </summary>
    private bool PushesImages
    {
        get
        {
            if (!PushImages)
                return false;

            var gitRef = Environment.GetEnvironmentVariable("GITHUB_REF");

            if (gitRef is "refs/heads/main")
                return true;

            Logger.LogInformation("Building the images without pushing them: {Ref} is not refs/heads/main",
                string.IsNullOrEmpty(gitRef)
                    ? "this is not a GitHub workflow run, and the ref"
                    : gitRef);

            return false;
        }
    }

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
                        await ProcessRunner.RunAsync(new("docker", ["push", $"{webRepository}:{tag}"]),
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

        await ProcessRunner.RunAsync(new("dotnet", arguments)
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
    ///     build context so that nothing else in the repository can reach the image.
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
        RootedFileSystem.CopyDirectory(site, context / "site");

        await ProcessRunner.RunAsync(new("docker",
            [
                "build",
                // A fresh base image on every build brings Caddy's and Alpine's patches in
                "--pull",
                .. tags.SelectMany(tag => new[] { "-t", $"{repository}:{tag}" }),
                "--label",
                $"org.opencontainers.image.revision={revision}",
                "--label",
                $"org.opencontainers.image.version={version}",
                context.ToString(),
            ])
            {
                OutputLogLevel = LogLevel.Information,
                // BuildKit writes its progress to stderr, which is not a warning
                ErrorLogLevel = LogLevel.Information,
            },
            cancellationToken);
    }

    /// <summary>
    ///     Checks the registry for each of <paramref name="tags" /> before anything is pushed to it. A tag is never
    ///     moved to another commit: if one already names an image of another revision the build fails.
    /// </summary>
    /// <returns>false when every tag already names an image of this revision, so there is nothing to push.</returns>
    private async Task<bool> CheckTagsAreFree(
        string repository,
        string[] tags,
        string revision,
        CancellationToken cancellationToken)
    {
        var anyMissing = false;

        foreach (var tag in tags)
        {
            var existingRevision = await GetRegistryRevision($"{repository}:{tag}", cancellationToken);

            if (existingRevision is null)
                anyMissing = true;
            else if (!string.Equals(existingRevision, revision, StringComparison.OrdinalIgnoreCase))
                throw new StepFailedException($"{repository}:{tag} already exists for revision {existingRevision}, " +
                                              $"not {revision}; a tag is never moved");
            else
                Logger.LogInformation("{Image}:{Tag} already exists for this revision", repository, tag);
        }

        return anyMissing;
    }

    /// <summary>
    ///     Reads the <c>org.opencontainers.image.revision</c> label of an image in the registry.
    /// </summary>
    /// <returns>
    ///     The label's value, an empty string for an image without one, or null when the registry has no such tag.
    /// </returns>
    private async Task<string?> GetRegistryRevision(string image, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(new("docker",
                ["buildx", "imagetools", "inspect", image, "--format", "{{json .Image}}"])
            {
                AllowFailedResult = true,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        if (result.ExitCode is not 0)
        {
            // Only a missing tag counts as free, or a missing repository (the registry's NAME_UNKNOWN) before an
            // image's first push; any other error, a refused login included, must not let a push go ahead
            if (result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("name unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new StepFailedException($"Could not read {image} from the registry: {result.Error.Trim()}");
        }

        using var document = JsonDocument.Parse(result.Output);
        var config = document.RootElement;

        // An image index (several platforms, or attestations) gives one image per platform
        if (!config.TryGetProperty("config", out _) &&
            config.ValueKind is JsonValueKind.Object &&
            config.TryGetProperty("linux/amd64", out var platformImage))
            config = platformImage;

        return config.TryGetProperty("config", out var imageConfig) &&
               imageConfig.TryGetProperty("Labels", out var labels) &&
               labels.ValueKind is JsonValueKind.Object &&
               labels.TryGetProperty("org.opencontainers.image.revision", out var label)
            ? label.GetString() ?? string.Empty
            : string.Empty;
    }
}
