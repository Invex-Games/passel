using System.Text.Json;

namespace Atom.Helpers;

/// <summary>
///     The container registry the self-hosted deployment's images live in (ADR 0004), and the rules for pushing to it:
///     images are pushed from main only, and a version tag already in the registry is never moved to another commit.
/// </summary>
internal interface IContainerRegistryHelper : IImageTagHelper
{
    const string WebApiImageName = "passel-webapi";

    const string WebImageName = "passel-web";

    const string DeployImageName = "passel-deploy";

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
    bool PushesImages
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

    /// <summary>
    ///     The name of one of the deployment's images in the registry, without a tag.
    /// </summary>
    string RegistryRepository(string imageName) => $"{ContainerRegistry}/{ContainerNamespace}/{imageName}";

    /// <summary>
    ///     Checks the registry for each of <paramref name="tags" /> before anything is pushed to it. A tag is never
    ///     moved to another commit: if one already names an image of another revision the build fails.
    /// </summary>
    /// <returns>false when every tag already names an image of this revision, so there is nothing to push.</returns>
    async Task<bool> CheckTagsAreFree(
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
                CommandLine.Join(["buildx", "imagetools", "inspect", image, "--format", "{{json .Image}}"]))
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
