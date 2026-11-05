namespace Atom.Targets;

/// <summary>
///     Checks the self-hosted deployment's compose files and scripts (<c>deploy/compose</c>) without running them:
///     <c>docker compose config</c> over the base file alone and with each override, every profile on, against the
///     placeholders in <c>ci.env</c>, and shellcheck over the scripts.
/// </summary>
internal interface IValidateCompose : IBuildAccessor
{
    private RootedPath ComposeDirectory => RootedFileSystem.AtomRootDirectory / "deploy" / "compose";

    /// <summary>
    ///     The scripts that ship in the bundle, relative to <see cref="ComposeDirectory" />.
    /// </summary>
    private static string[] Scripts => ["deploy.sh", "deploy-gate.sh", "backup.sh", "db/roles.sh"];

    /// <summary>
    ///     The pinned shellcheck image, for a machine without shellcheck installed. GitHub's Ubuntu runners have it.
    /// </summary>
    private const string ShellcheckImage = "koalaman/shellcheck:stable";

    Target ValidateCompose =>
        t => t
            .DescribedAs("Checks the compose files with docker compose config and the deploy scripts with shellcheck")
            .Executes(async cancellationToken =>
            {
                string[][] fileSets =
                [
                    ["compose.yaml"],
                    ["compose.yaml", "compose.ports.yaml"],
                    ["compose.yaml", "compose.external-proxy.yaml"],
                ];

                foreach (var files in fileSets)
                {
                    Logger.LogInformation("Checking {Files}", string.Join(" + ", files));

                    await ProcessRunner.RunAsync(new("docker",
                        CommandLine.Join([
                            "compose",
                            .. files.SelectMany(file => new[] { "--file", file }),
                            "--env-file",
                            "ci.env",
                            "--profile",
                            "edge",
                            "--profile",
                            "telemetry",
                            "config",
                            "--quiet",
                        ]))
                        {
                            WorkingDirectory = ComposeDirectory,
                        },
                        cancellationToken);
                }

                var shellcheck = FindShellcheck();

                if (shellcheck is not null)
                    await ProcessRunner.RunAsync(new(shellcheck, CommandLine.Join(Scripts))
                        {
                            WorkingDirectory = ComposeDirectory,
                        },
                        cancellationToken);
                else
                    await ProcessRunner.RunAsync(new("docker",
                        CommandLine.Join([
                            "run",
                            "--rm",
                            "--volume",
                            $"{ComposeDirectory}:/mnt:ro",
                            "--workdir",
                            "/mnt",
                            ShellcheckImage,
                            .. Scripts,
                        ]))
                        {
                            ErrorLogLevel = LogLevel.Information,
                        },
                        cancellationToken);

                Logger.LogInformation("The compose files and the {Count} scripts are valid", Scripts.Length);
            });

    /// <summary>
    ///     Looks for shellcheck on the PATH directly: a lookup with which or where logs a warning when it is missing,
    ///     which is the usual case on a developer's machine.
    /// </summary>
    private static string? FindShellcheck()
    {
        var name = OperatingSystem.IsWindows()
            ? "shellcheck.exe"
            : "shellcheck";

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}
