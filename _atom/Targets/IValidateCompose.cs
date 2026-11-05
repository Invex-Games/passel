using System.Text.Json;

namespace Atom.Targets;

/// <summary>
///     Checks the self-hosted deployment's compose files and scripts (<c>deploy/compose</c>) without running them:
///     <c>docker compose config</c> over the base file alone and with each override, every profile on, against the
///     placeholders in <c>ci.env</c> and in <c>ci.preprod.env</c>, a second stack on the same server; what compose
///     makes of each stack's settings; and shellcheck over the scripts, the web image's entrypoint among them.
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

    /// <summary>
    ///     The placeholder settings of two stacks on one server and what compose must make of each. <c>ci.env</c>
    ///     leaves every per-stack setting at its default, production's, which must stay what it was before stacks were
    ///     configurable. <c>ci.preprod.env</c> sets each one as a pre-prod stack beside it would, and leaves
    ///     <c>PROXY_TRUSTED_CIDR</c> out, so the API must trust that stack's own edge address.
    /// </summary>
    private static StackExpectation[] Stacks =>
    [
        new("ci.env",
            "passel",
            "172.30.1.0/24",
            "172.30.1.1",
            "172.30.0.0/24",
            "172.30.0.1",
            "172.30.0.128/25",
            "172.30.0.2",
            "passel-api",
            "passel-web",
            "172.30.0.2/32",
            "18080",
            "18081"),
        new("ci.preprod.env",
            "passel-preprod",
            "172.30.3.0/24",
            "172.30.3.1",
            "172.30.2.0/24",
            "172.30.2.1",
            "172.30.2.128/25",
            "172.30.2.2",
            "passel-preprod-api",
            "passel-preprod-web",
            "172.30.2.2/32",
            "18082",
            "18083"),
    ];

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

                foreach (var stack in Stacks)
                foreach (var files in fileSets)
                {
                    Logger.LogInformation("Checking {Files} with {EnvFile}", string.Join(" + ", files), stack.EnvFile);

                    await ProcessRunner.RunAsync(new("docker",
                        CommandLine.Join([
                            "compose",
                            .. files.SelectMany(file => new[] { "--file", file }),
                            "--env-file",
                            stack.EnvFile,
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

                await CheckWebEnvironment(cancellationToken);

                foreach (var stack in Stacks)
                    await CheckStack(stack, cancellationToken);

                var shellcheck = FindShellcheck();

                if (shellcheck is not null)
                    await ProcessRunner.RunAsync(new(shellcheck, CommandLine.Join(Scripts))
                        {
                            WorkingDirectory = DeployDirectory,
                        },
                        cancellationToken);
                else
                    await ProcessRunner.RunAsync(new("docker",
                        CommandLine.Join([
                            "run",
                            "--rm",
                            "--volume",
                            $"{DeployDirectory}:/mnt:ro",
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
    ///     The web image is the same in every environment and is configured from the server's .env, so compose must
    ///     hand it the API's origin, derived from API_HOST, and the web client id: with ci.env's placeholders,
    ///     https://api.example.test and ci-web.apps.googleusercontent.com.
    /// </summary>
    private async Task CheckWebEnvironment(CancellationToken cancellationToken)
    {
        using var document = await ComposeConfig("ci.env", ["compose.yaml"], [], cancellationToken);

        var environment = document.RootElement.GetProperty("services").GetProperty("web").GetProperty("environment");

        if (StringProperty(environment, "API_ORIGIN") is not "https://api.example.test" ||
            StringProperty(environment, "GOOGLE_CLIENT_ID") is not "ci-web.apps.googleusercontent.com")
            throw new StepFailedException("The web service must get API_ORIGIN=https://<API_HOST> and " +
                                          "GOOGLE_CLIENT_ID=<GOOGLE_WEB_CLIENT_ID> from .env; with ci.env it got " +
                                          $"API_ORIGIN='{StringProperty(environment, "API_ORIGIN")}', " +
                                          $"GOOGLE_CLIENT_ID='{StringProperty(environment, "GOOGLE_CLIENT_ID")}'");
    }

    /// <summary>
    ///     Checks what compose makes of one stack's settings: its project name and so its volumes, its networks'
    ///     subnets, the bundled edge's fixed address and the API's trust of it, the aliases on the edge network, and
    ///     with <c>compose.ports.yaml</c> the loopback ports and the trust of both gateways.
    /// </summary>
    private async Task CheckStack(StackExpectation stack, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        void Expect(string what, string? actual, string expected)
        {
            if (actual != expected)
                problems.Add($"{what} is '{actual}', expected '{expected}'");
        }

        using (var document = await ComposeConfig(stack.EnvFile, ["compose.yaml"], ["edge"], cancellationToken))
        {
            var root = document.RootElement;
            var services = root.GetProperty("services");
            var api = services.GetProperty("api");
            var edge = services.GetProperty("edge");
            var networks = root.GetProperty("networks");

            Expect("The project name", StringProperty(root, "name"), stack.Project);

            Expect("The pgdata volume",
                StringProperty(root.GetProperty("volumes").GetProperty("pgdata"), "name"),
                $"{stack.Project}_pgdata");

            var backend = networks.GetProperty("backend").GetProperty("ipam").GetProperty("config")[0];
            Expect("The backend subnet", StringProperty(backend, "subnet"), stack.BackendSubnet);
            Expect("The backend gateway", StringProperty(backend, "gateway"), stack.BackendGateway);

            var edgeNetwork = networks.GetProperty("edge").GetProperty("ipam").GetProperty("config")[0];
            Expect("The edge subnet", StringProperty(edgeNetwork, "subnet"), stack.EdgeSubnet);
            Expect("The edge gateway", StringProperty(edgeNetwork, "gateway"), stack.EdgeGateway);
            Expect("The edge ip_range", StringProperty(edgeNetwork, "ip_range"), stack.EdgeIpRange);

            Expect("The edge's address",
                StringProperty(edge.GetProperty("networks").GetProperty("edge"), "ipv4_address"),
                stack.EdgeAddress);

            Expect("The API's alias", FirstAlias(api), stack.ApiAlias);
            Expect("The web app's alias", FirstAlias(services.GetProperty("web")), stack.WebAlias);

            Expect("The API's trusted proxy",
                StringProperty(api.GetProperty("environment"), "ForwardedHeaders__KnownNetworks__0"),
                stack.TrustedCidr);

            // The bundled edge's Caddyfile proxies to the aliases it is given
            var edgeEnvironment = edge.GetProperty("environment");
            Expect("The edge's PASSEL_API_ALIAS", StringProperty(edgeEnvironment, "PASSEL_API_ALIAS"), stack.ApiAlias);
            Expect("The edge's PASSEL_WEB_ALIAS", StringProperty(edgeEnvironment, "PASSEL_WEB_ALIAS"), stack.WebAlias);
        }

        using (var document = await ComposeConfig(stack.EnvFile,
                   ["compose.yaml", "compose.ports.yaml"],
                   [],
                   cancellationToken))
        {
            var services = document.RootElement.GetProperty("services");
            var environment = services.GetProperty("api").GetProperty("environment");

            Expect("With compose.ports.yaml, the API's first trusted network",
                StringProperty(environment, "ForwardedHeaders__KnownNetworks__0"),
                $"{stack.EdgeGateway}/32");

            Expect("With compose.ports.yaml, the API's second trusted network",
                StringProperty(environment, "ForwardedHeaders__KnownNetworks__1"),
                $"{stack.BackendGateway}/32");

            Expect("The API's loopback port", PublishedPort(services.GetProperty("api")), stack.ApiPort);
            Expect("The web app's loopback port", PublishedPort(services.GetProperty("web")), stack.WebPort);
        }

        if (problems.Count > 0)
            throw new StepFailedException($"With {stack.EnvFile}: " + string.Join("; ", problems));

        Logger.LogInformation("{EnvFile} configures the stack {Project} as expected", stack.EnvFile, stack.Project);

        static string? FirstAlias(JsonElement service) =>
            service.GetProperty("networks").GetProperty("edge").TryGetProperty("aliases", out var aliases) &&
            aliases.GetArrayLength() > 0
                ? aliases[0].GetString()
                : null;

        static string? PublishedPort(JsonElement service) =>
            service.TryGetProperty("ports", out var ports) && ports.GetArrayLength() > 0
                ? StringProperty(ports[0], "published")
                : null;
    }

    /// <summary>
    ///     The resolved compose model, as <c>docker compose config --format json</c> prints it.
    /// </summary>
    private async Task<JsonDocument> ComposeConfig(
        string envFile,
        string[] files,
        string[] profiles,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(new("docker",
                CommandLine.Join([
                    "compose",
                    .. files.SelectMany(file => new[] { "--file", file }),
                    "--env-file",
                    envFile,
                    .. profiles.SelectMany(profile => new[] { "--profile", profile }),
                    "config",
                    "--format",
                    "json",
                ]))
            {
                WorkingDirectory = ComposeDirectory,
                OutputLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        return JsonDocument.Parse(result.Output);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>
    ///     One stack's placeholder settings file and the values compose must resolve from it.
    /// </summary>
    private sealed record StackExpectation(
        string EnvFile,
        string Project,
        string BackendSubnet,
        string BackendGateway,
        string EdgeSubnet,
        string EdgeGateway,
        string EdgeIpRange,
        string EdgeAddress,
        string ApiAlias,
        string WebAlias,
        string TrustedCidr,
        string ApiPort,
        string WebPort);
}
