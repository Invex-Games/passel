using Invex.Atom.Build.Reports;

namespace Atom.Targets;

/// <summary>
///     Packs the compose files and scripts of the self-hosted deployment (<c>deploy/compose</c>) into the
///     <c>passel-deploy</c> image and, on main, pushes it with the API and web images' tags. The server's deploy gate
///     extracts the bundle of the tag it deploys, so every release, a rollback's included, runs with the compose files
///     it was built with. Nothing ever runs the image: it is built <c>FROM scratch</c> and holds files only.
/// </summary>
internal interface IPublishDeployBundle : ITestTargets, ISetupBuildInfo, IContainerRegistryHelper, IReportsHelper
{
    /// <summary>
    ///     The bundle's sources, which are also its build context. The context's .dockerignore lets in only the files
    ///     that ship.
    /// </summary>
    private RootedPath ComposeDirectory => RootedFileSystem.AtomRootDirectory / "deploy" / "compose";

    Target PublishDeployBundle =>
        t => t
            .DescribedAs("Builds the compose bundle image and, on main, pushes it to the container registry")
            .DependsOn(nameof(TestWebApi))
            .UsesParam(nameof(ContainerRegistry))
            .UsesParam(nameof(ContainerNamespace))
            .UsesParam(nameof(PushImages))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildVersion))
            .Executes(async cancellationToken =>
            {
                var push = PushesImages;
                var version = ImageTag;
                var revision = SourceRevision;
                string[] tags = [version, ShaTag];

                // The same local names as the other images when not pushing; only a push names the registry
                var repository = push
                    ? RegistryRepository(DeployImageName)
                    : $"{ContainerNamespace}/{DeployImageName}";

                await ProcessRunner.RunAsync(new("docker",
                    [
                        "build",
                        "--file",
                        (ComposeDirectory / "Dockerfile.bundle").ToString(),
                        .. tags.SelectMany(tag => new[] { "-t", $"{repository}:{tag}" }),
                        "--label",
                        $"org.opencontainers.image.revision={revision}",
                        "--label",
                        $"org.opencontainers.image.version={version}",
                        ComposeDirectory.ToString(),
                    ])
                    {
                        OutputLogLevel = LogLevel.Information,
                        // BuildKit writes its progress to stderr, which is not a warning
                        ErrorLogLevel = LogLevel.Information,
                    },
                    cancellationToken);

                var pushed = push && await CheckTagsAreFree(repository, tags, revision, cancellationToken);

                if (pushed)
                    foreach (var tag in tags)
                        await ProcessRunner.RunAsync(new("docker", ["push", $"{repository}:{tag}"]), cancellationToken);

                var outcome = push switch
                {
                    false => "built locally, not pushed",
                    true when pushed => "pushed",
                    true => "already pushed",
                };

                AddReportData(new ListReportData([.. tags.Select(tag => $"{repository}:{tag} ({outcome})")])
                {
                    Title = $"Deploy bundle {version}",
                });

                Logger.LogInformation("Deploy bundle {Version} ({Revision}) {Outcome}", version, revision, outcome);
            });
}
