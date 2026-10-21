namespace Atom.Targets;

internal interface ISetupDevEnvironment : IChromeExtensionHelper, IFlutterHelper, IMobileAppHelper
{
    Target SetupDevEnvironment =>
        t => t
            .DescribedAs("Checks the tools local development needs and prepares local config and packages")
            .Executes(async cancellationToken =>
            {
                List<string> missingTools = [];

                async Task<RootedPath?> CheckTool(string command, string purpose, string installHint, bool required)
                {
                    if (await TryFindEnvironmentPath(command, cancellationToken) is { } path)
                    {
                        Logger.LogInformation("Found {Command} ({Purpose})", command, purpose);

                        return path;
                    }

                    if (required)
                        missingTools.Add(command);

                    Logger.Log(required
                            ? LogLevel.Error
                            : LogLevel.Warning,
                        "{Command} not found; needed for {Purpose}. Install: {InstallHint}",
                        command,
                        purpose,
                        installHint);

                    return null;
                }

                var dockerPath = await CheckTool("docker",
                    "the Postgres container",
                    "https://docs.docker.com/get-started/get-docker/",
                    true);

                var npmPath = await CheckTool("npm", "the Chrome Extension", "https://nodejs.org/", true);

                await CheckTool("aspire",
                    "`aspire run` (optional, `dotnet run --project src/webapi/Invex.Passel.AppHost` also works)",
                    "https://aspire.dev/get-started/install-cli/",
                    false);

                var flutterPath = await CheckTool("flutter",
                    "the mobile app",
                    "https://docs.flutter.dev/get-started/install",
                    false);

                await CheckTool("devtunnel",
                    "reaching the API from a phone",
                    "https://learn.microsoft.com/azure/developer/dev-tunnels/get-started",
                    false);

                if (dockerPath is not null)
                {
                    var dockerInfo = await ProcessRunner.RunAsync(new(dockerPath, "info")
                        {
                            AllowFailedResult = true,
                            InvocationLogLevel = LogLevel.Debug,
                            OutputLogLevel = LogLevel.Debug,
                            ErrorLogLevel = LogLevel.Debug,
                        },
                        cancellationToken);

                    if (dockerInfo.ExitCode is not 0)
                        Logger.LogWarning("Docker is installed but not running; start it before running the AppHost");
                }

                var devCertificate = await ProcessRunner.RunAsync(new("dotnet", "dev-certs https --check --trust")
                    {
                        AllowFailedResult = true,
                        InvocationLogLevel = LogLevel.Debug,
                        OutputLogLevel = LogLevel.Debug,
                        ErrorLogLevel = LogLevel.Debug,
                    },
                    cancellationToken);

                if (devCertificate.ExitCode is 0)
                    Logger.LogInformation("The ASP.NET Core HTTPS development certificate is trusted");
                else
                    Logger.LogWarning(
                        "The ASP.NET Core HTTPS development certificate is missing or untrusted; run `dotnet dev-certs https --trust`");

                var mobileEnvFile = MobileAppDirectory / ".env";

                if (mobileEnvFile.FileExists)
                {
                    Logger.LogInformation("{File} already exists", mobileEnvFile);
                }
                else
                {
                    RootedFileSystem.File.Copy(MobileAppDirectory / ".env.example", mobileEnvFile);
                    Logger.LogWarning("Created {File} from .env.example; fill in its values before running the app",
                        mobileEnvFile);
                }

                if (npmPath is not null)
                {
                    Logger.LogInformation("Installing Chrome Extension packages");
                    await RestoreChromeExtensionPackages(cancellationToken);
                }

                if (flutterPath is not null)
                {
                    Logger.LogInformation("Fetching mobile app packages");

                    await ProcessRunner.RunAsync(new(flutterPath, "pub get")
                        {
                            WorkingDirectory = MobileAppDirectory,
                            OutputLogLevel = LogLevel.Debug,
                        },
                        cancellationToken);
                }

                if (missingTools.Count > 0)
                    throw new StepFailedException($"Missing required tools: {string.Join(", ", missingTools)}");

                Logger.LogInformation(
                    "Ready. Start everything with `aspire run` (or `dotnet run --project src/webapi/Invex.Passel.AppHost`); the dashboard asks for any Google OAuth values it doesn't have yet");
            });
}
