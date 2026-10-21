namespace Atom.Helpers;

internal interface IFlutterHelper : IProcessHelper
{
    private static readonly BuildCache<string> FlutterCommandCache = new();

    [ParamDefinition("flutter-version", "The version of Flutter to use")]
    string FlutterVersion => GetParam(() => FlutterVersion, "latest");

    private SemVer? ParseFlutterVersion =>
        FlutterVersion is "latest" or "" or null
            ? null
            : SemVer.TryParse(FlutterVersion, out var v)
                ? v
                : throw new StepFailedException($"Invalid Flutter version '{FlutterVersion}'");

    private RootedPath ManualInstallPath => RootedFileSystem.AtomRootDirectory / ".flutter";

    private RootedPath ManualCommandPath =>
        ManualInstallPath /
        "bin" /
        (Environment.OSVersion.Platform is PlatformID.Win32NT
            ? "flutter.bat"
            : "flutter");

    async Task FlutterBuild(
        string[] args,
        RootedPath? workingDirectory = null,
        Dictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        // Kept between builds, so packages are only downloaded when they change
        var pubCache = RootedFileSystem.AtomRootDirectory / ".pub-cache";

        RootedFileSystem.Directory.CreateDirectory(pubCache);

        Environment.SetEnvironmentVariable("PUB_CACHE", pubCache);

        await EnsureFlutterIsCorrectVersion(cancellationToken);
        await Task.Delay(2000, cancellationToken);

        var commandPath = await GetFlutterCommand(cancellationToken);

        await ProcessRunner.RunAsync(new(commandPath, "pub get")
            {
                WorkingDirectory = workingDirectory ?? RootedFileSystem.AtomRootDirectory,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        await Task.Delay(2000, cancellationToken);

        await ProcessRunner.RunAsync(new(commandPath, args)
            {
                WorkingDirectory = workingDirectory ?? RootedFileSystem.AtomRootDirectory,
                OutputLogLevel = LogLevel.Information,
                EnvironmentVariables = environmentVariables ?? [],
            },
            cancellationToken);
    }

    async ValueTask<string> GetFlutterCommand(CancellationToken cancellationToken = default) =>
        await TryGetFlutterCommand(cancellationToken) ??
        throw new StepFailedException("Failed to find Flutter command");

    async ValueTask<string> GetDartCommand(CancellationToken cancellationToken = default) =>
        await TryGetDartCommand(cancellationToken) ?? throw new StepFailedException("Failed to find Dart command");

    async ValueTask<string?> TryGetFlutterCommand(CancellationToken cancellationToken = default)
    {
        if (FlutterCommandCache.TryGetValue(this, out var cachedPath))
            return cachedPath;

        var manualInstallBinPath = ManualInstallPath / "bin";

        if (manualInstallBinPath.DirectoryExists)
            return Environment.OSVersion.Platform is PlatformID.Win32NT
                ? manualInstallBinPath / "flutter.bat"
                : manualInstallBinPath / "flutter";

        if (await TryFindEnvironmentPath("flutter", cancellationToken) is { } environmentPath)
            return FlutterCommandCache.Set(this, environmentPath.Path);

        var path = Environment.GetEnvironmentVariable("PATH") ??
                   throw new StepFailedException("Failed to get PATH environment variable");

        var flutterPathString = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? path
                .Split(';')
                .FirstOrDefault(x => x.Contains("flutter.bat"))
            : path
                .Split(':')
                .FirstOrDefault(x => x.Contains("flutter"));

        return flutterPathString switch
        {
            { Length: > 0 } when flutterPathString.EndsWith("bin") ||
                                 flutterPathString.EndsWith("bin/") ||
                                 flutterPathString.EndsWith("bin\\") => FlutterCommandCache.Set(this,
                Environment.OSVersion.Platform is PlatformID.Win32NT
                    ? RootedFileSystem.CreateRootedPath(flutterPathString) / "flutter.bat"
                    : RootedFileSystem.CreateRootedPath(flutterPathString) / "flutter"),
            { Length: > 0 } => FlutterCommandCache.Set(this, RootedFileSystem.CreateRootedPath(flutterPathString)),
            _ => null,
        };
    }

    async ValueTask<string?> TryGetDartCommand(CancellationToken cancellationToken = default)
    {
        var flutterCommandPathString = await GetFlutterCommand(cancellationToken);
        var flutterCommandPath = RootedFileSystem.CreateRootedPath(flutterCommandPathString);
        var flutterDir = flutterCommandPath.Parent!;

        var dartPath = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? flutterDir / "cache" / "dart-sdk" / "bin" / "dart.exe"
            : flutterDir / "cache" / "dart-sdk" / "bin" / "dart";

        return dartPath.FileExists
            ? dartPath.Path
            : null;
    }

    async Task EnsureFlutterIsCorrectVersion(CancellationToken cancellationToken = default)
    {
        var (flutterCommandPath, wasInstalled) = await EnsureFlutterExists(cancellationToken);
        var version = ParseFlutterVersion;

        if (wasInstalled)
        {
            if (version is null)
            {
                await ProcessRunner.RunAsync(new(ManualCommandPath, "upgrade -f"), cancellationToken);

                return;
            }

            var flutterVersionResult =
                await ProcessRunner.RunAsync(new(flutterCommandPath, "--version"), cancellationToken);

            if (!flutterVersionResult.Output.Contains($"Flutter {version}"))
                await ProcessRunner.RunAsync(new(ManualCommandPath, "doctor"), cancellationToken);

            return;
        }

        var path = flutterCommandPath.Parent!;

        if (path.Path.EndsWith("bin") || path.Path.EndsWith("bin/") || path.Path.EndsWith("bin\\"))
            path = path.Parent!;

        if (version is null)
        {
            await UpgradeExistingFlutter(path, cancellationToken);
        }
        else
        {
            var flutterVersionResult =
                await ProcessRunner.RunAsync(new(flutterCommandPath, "--version"), cancellationToken);

            if (!flutterVersionResult.Output.Contains($"Flutter {version}"))
                await SetExistingFlutterVersion(path, version, cancellationToken);
        }
    }

    private async Task<(RootedPath path, bool wasInstalled)> EnsureFlutterExists(
        CancellationToken cancellationToken = default)
    {
        var path = await TryGetFlutterCommand(cancellationToken);

        if (path is not null)
            return (RootedFileSystem.CreateRootedPath(path), false);

        Logger.LogInformation("Flutter not found in PATH, installing manually");
        await InstallManualFlutter(ParseFlutterVersion, cancellationToken);

        return (ManualCommandPath, true);
    }

    private async Task InstallManualFlutter(SemVer? version, CancellationToken cancellationToken = default)
    {
        // If we can't find flutter, we need to install it
        // We'll do this by cloning the flutter repo
        var branch = version is null
            ? string.Empty
            : $" --branch {version}";

        await ProcessRunner.RunAsync(
            new("git", $"clone https://github.com/flutter/flutter.git{branch} --depth 1 .flutter")
            {
                WorkingDirectory = RootedFileSystem.AtomRootDirectory,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        var manualInstallPath = ManualInstallPath;

        Logger.LogInformation("Flutter installed to {FlutterPath}", manualInstallPath);

        await ProcessRunner.RunAsync(new(ManualCommandPath,
                version is null
                    ? "upgrade -f"
                    : "doctor")
            {
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);
    }

    private async Task UpgradeExistingFlutter(RootedPath flutterDir, CancellationToken cancellationToken = default)
    {
        // Find git
        var gitPath = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? await FindEnvironmentPath("git", cancellationToken)
            : "git";

        // Ensure no working changes
        await ProcessRunner.RunAsync(new(gitPath, "clean --force")
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        await ProcessRunner.RunAsync(new(gitPath, "restore .")
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        var flutterPath = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? flutterDir / "bin" / "flutter.bat"
            : flutterDir / "bin" / "flutter";

        var runResult = await ProcessRunner.RunAsync(new(flutterPath, "upgrade -f")
            {
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        if (runResult.ExitCode is not 0)
            throw new StepFailedException("Failed to upgrade Flutter");
    }

    private async Task SetExistingFlutterVersion(
        RootedPath flutterDir,
        SemVer version,
        CancellationToken cancellationToken = default)
    {
        // Find git
        var gitPath = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? await FindEnvironmentPath("git", cancellationToken)
            : "git";

        // Fetch tag
        await ProcessRunner.RunAsync(new(gitPath, $"fetch origin refs/tags/{version}:refs/tags/{version}")
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        // Ensure no working changes
        await ProcessRunner.RunAsync(new(gitPath, "clean --force")
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        await ProcessRunner.RunAsync(new(gitPath, "restore .")
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        // Flutter branch is named after the version, so we can just check it out
        await ProcessRunner.RunAsync(new(gitPath, "checkout --force " + version)
            {
                WorkingDirectory = flutterDir,
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);

        var flutterPath = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? flutterDir / "bin" / "flutter.bat"
            : flutterDir / "bin" / "flutter";

        await ProcessRunner.RunAsync(new(flutterPath, "doctor")
            {
                OutputLogLevel = LogLevel.Debug,
                ErrorLogLevel = LogLevel.Debug,
            },
            cancellationToken);
    }
}
