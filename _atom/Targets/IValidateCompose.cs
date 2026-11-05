using System.Text.Json;

namespace Atom.Targets;

/// <summary>
///     Checks the self-hosted deployment's compose files and scripts (<c>deploy/compose</c>) without running them:
///     <c>docker compose config</c> over the base file alone and with each override, every profile on, against the
///     placeholders in <c>ci.env</c>, and shellcheck over the scripts, the web image's entrypoint among them.
/// </summary>
internal interface IValidateCompose : IBuildAccessor
{
    private RootedPath DeployDirectory => RootedFileSystem.AtomRootDirectory / "deploy";

    private RootedPath ComposeDirectory => DeployDirectory / "compose";

    /// <summary>
    ///     The scripts that ship in the bundle and the web image's entrypoint, relative to
    ///     <see cref="DeployDirectory" />.
    /// </summary>
    private static string[] Scripts =>
        ["compose/deploy.sh", "compose/deploy-gate.sh", "compose/backup.sh", "compose/db/roles.sh", "web/entrypoint.sh"];

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
                        [
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
                        ])
                        {
                            WorkingDirectory = ComposeDirectory,
                        },
                        cancellationToken);
                }

                await CheckWebEnvironment(cancellationToken);

                var shellcheck = FindShellcheck();

                if (shellcheck is not null)
                    await ProcessRunner.RunAsync(new(shellcheck, Scripts)
                        {
                            WorkingDirectory = DeployDirectory,
                        },
                        cancellationToken);
                else
                    await ProcessRunner.RunAsync(new("docker",
                        [
                            "run",
                            "--rm",
                            "--volume",
                            $"{DeployDirectory}:/mnt:ro",
                            "--workdir",
                            "/mnt",
                            ShellcheckImage,
                            .. Scripts,
                        ])
                        {
                            ErrorLogLevel = LogLevel.Information,
                        },
                        cancellationToken);

                Logger.LogInformation("The compose files and the {Count} scripts are valid", Scripts.Length);
            });

    /// <summary>
    ///     The web image is the same in every environment and is configured from the server's .env, so compose must
    ///     hand it the API's origin, derived from API_HOST, and the web client id: with ci.env's placeholders,
    ///     https://api.example.test and ci-web.apps.googleusercontent.com.
    /// </summary>
    private async Task CheckWebEnvironment(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(new("docker",
            ["compose", "--file", "compose.yaml", "--env-file", "ci.env", "config", "--format", "json"])
            {
                WorkingDirectory = ComposeDirectory,
                OutputLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        using var document = JsonDocument.Parse(result.Output);

        var environment = document.RootElement.GetProperty("services").GetProperty("web").GetProperty("environment");

        string? Value(string name) =>
            environment.TryGetProperty(name, out var value)
                ? value.GetString()
                : null;

        if (Value("API_ORIGIN") is not "https://api.example.test" ||
            Value("GOOGLE_CLIENT_ID") is not "ci-web.apps.googleusercontent.com")
            throw new StepFailedException("The web service must get API_ORIGIN=https://<API_HOST> and " +
                                          "GOOGLE_CLIENT_ID=<GOOGLE_WEB_CLIENT_ID> from .env; with ci.env it got " +
                                          $"API_ORIGIN='{Value("API_ORIGIN")}', " +
                                          $"GOOGLE_CLIENT_ID='{Value("GOOGLE_CLIENT_ID")}'");
    }

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
