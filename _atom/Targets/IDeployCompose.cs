using Invex.Atom.Build.Reports;

namespace Atom.Targets;

/// <summary>
///     Deploys a release to the owner's server over SSH (ADR 0004, docs/guides/deploying.md). The deploy key may only
///     run the server's deploy gate, which accepts <c>deploy &lt;tag&gt; [--no-migrate]</c> and a registry token on
///     stdin, pulls the release's compose bundle and runs its deploy script. Deploying an older tag with
///     <see cref="Migrate" /> off is a rollback.
/// </summary>
internal interface IDeployCompose : ISetupBuildInfo, IContainerRegistryHelper, IReportsHelper
{
    /// <summary>
    ///     The private key the gate's authorized_keys line accepts, in OpenSSH format.
    /// </summary>
    const string SshKeyVariable = "DEPLOY_SSH_KEY";

    /// <summary>
    ///     The server's host keys as known_hosts lines, checked out of band, so the connection never trusts a key it
    ///     sees for the first time.
    /// </summary>
    const string KnownHostsVariable = "DEPLOY_KNOWN_HOSTS";

    /// <summary>
    ///     A token that may read the images, which the gate signs in to the registry with for this deploy only.
    /// </summary>
    const string PullTokenVariable = "GHCR_PULL_TOKEN";

    [ParamDefinition("image-tag",
        "The image tag to deploy; empty for this build's version. An older tag, with migrate off, rolls back")]
    string? DeployImageTag => GetParam(() => DeployImageTag);

    [ParamDefinition("migrate", "Whether the release's db-migrate runs before the API starts; off for a rollback")]
    bool Migrate => GetParam(() => Migrate, true, value => !bool.TryParse(value, out var migrate) || migrate);

    [ParamDefinition("deploy-host", "The server the release is deployed to")]
    string? DeployHost => GetParam(() => DeployHost);

    [ParamDefinition("deploy-user", "The user the deploy key signs in to the server as")]
    string? DeployUser => GetParam(() => DeployUser);

    Target DeployCompose =>
        t => t
            .DescribedAs("Deploys a release to the self-hosted server over SSH through its deploy gate")
            .UsesParam(nameof(DeployImageTag))
            .UsesParam(nameof(Migrate))
            .RequiresParam(nameof(DeployHost))
            .RequiresParam(nameof(DeployUser))
            .UsesParam(nameof(ContainerRegistry))
            .UsesParam(nameof(ContainerNamespace))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildVersion))
            .Executes(async cancellationToken =>
            {
                var tag = string.IsNullOrWhiteSpace(DeployImageTag)
                    ? ImageTag
                    : DeployImageTag.Trim();

                // The gate and deploy.sh check the same pattern; checking it here fails the run before it signs in
                if (!IImageTagHelper.IsValidTag(tag))
                    throw new StepFailedException($"'{tag}' is not a valid image tag");

                var sshKey = RequireVariable(SshKeyVariable);
                var knownHosts = RequireVariable(KnownHostsVariable);
                RequireVariable(PullTokenVariable);

                // A release is its three images; a tag that was never pushed from main has none of them
                string[] images = [WebApiImageName, WebImageName, DeployImageName];

                foreach (var image in images)
                    await RequireRegistryImage($"{RegistryRepository(image)}:{tag}", cancellationToken);

                var sshDirectory = RootedFileSystem.AtomTempDirectory / "deploy-ssh";
                RootedFileSystem.CleanOrCreateDirectory(sshDirectory);

                var keyFile = sshDirectory / "id_deploy";
                var knownHostsFile = sshDirectory / "known_hosts";

                try
                {
                    WritePrivateFile(keyFile, sshKey);
                    WritePrivateFile(knownHostsFile, knownHosts);

                    string[] request = Migrate
                        ? ["deploy", tag]
                        : ["deploy", tag, "--no-migrate"];

                    Logger.LogInformation("Deploying {Tag} to {Host} ({Migration})",
                        tag,
                        DeployHost,
                        Migrate
                            ? "with db-migrate"
                            : "without db-migrate");

                    // The token goes to the gate on stdin, from the environment, so it is never on a command line;
                    // the arguments stay a list, passed to ssh as "$@"
                    await ProcessRunner.RunAsync(new("bash",
                        CommandLine.Join([
                            "-c",
                            $"printf '%s\\n' \"${PullTokenVariable}\" | ssh \"$@\"",
                            "ssh",
                            "-o",
                            "BatchMode=yes",
                            "-o",
                            "IdentitiesOnly=yes",
                            "-o",
                            "StrictHostKeyChecking=yes",
                            "-o",
                            $"UserKnownHostsFile={knownHostsFile}",
                            "-o",
                            "ConnectTimeout=30",
                            "-o",
                            "ServerAliveInterval=30",
                            "-i",
                            keyFile.ToString(),
                            "-T",
                            $"{DeployUser}@{DeployHost}",
                            .. request,
                        ]))
                        {
                            OutputLogLevel = LogLevel.Information,
                            // Compose writes its progress to stderr, which is not a warning
                            ErrorLogLevel = LogLevel.Information,
                        },
                        cancellationToken);
                }
                finally
                {
                    RootedFileSystem.Directory.Delete(sshDirectory, true);
                }

                AddReportData(new TextReportData($"Deployed {tag} to {DeployHost}" + (Migrate
                    ? string.Empty
                    : " without db-migrate"))
                {
                    Title = "Deployment",
                });
            });

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value)
            ? throw new StepFailedException($"{name} must be set (see docs/guides/deploying.md)")
            : value;
    }

    /// <summary>
    ///     Fails unless the registry has <paramref name="image" />, before anything reaches the server.
    /// </summary>
    private async Task RequireRegistryImage(string image, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(new("docker", CommandLine.Join(["manifest", "inspect", image]))
            {
                AllowFailedResult = true,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        if (result.ExitCode is not 0)
            throw new StepFailedException($"{image} is not in the registry: {result.Error.Trim()}. " +
                                          "Only builds of main push images");

        Logger.LogInformation("{Image} is in the registry", image);
    }

    /// <summary>
    ///     Writes a file only its owner can read, with a final newline, which ssh needs at the end of a key.
    /// </summary>
    private static void WritePrivateFile(RootedPath path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var writer = new StreamWriter(path.ToString(), options);
        writer.Write(content.ReplaceLineEndings("\n").TrimEnd('\n') + "\n");
    }
}
