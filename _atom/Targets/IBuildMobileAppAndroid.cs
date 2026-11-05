namespace Atom.Targets;

internal interface IBuildMobileAppAndroid : IFlutterHelper,
    IAndroidKeystoreHelper,
    IAndroidSymbolsHelper,
    ISetupBuildInfo,
    IMobileAppHelper,
    IConfigurationHelper
{
    const string AndroidAppArtifactName = "android";
    const string AndroidDartSymbolsName = "dart-symbols";

    Target BuildMobileAppAndroid =>
        t => t
            .DescribedAs("Builds the android app for the configured package type and flavor")
            .RequiresParam(nameof(MobileAppFlavor))
            .RequiresParam(nameof(AndroidKeystoreAlias))
            .RequiresParam(nameof(AndroidKeystorePassword))
            .UsesParam(nameof(ServerClientId))
            .UsesParam(nameof(MobileAppClientId))
            .UsesParam(nameof(ApiBaseUrlProduction))
            .UsesParam(nameof(ApiBaseUrlDevelopment))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildTimestamp))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildVersion))
            .ProducesArtifact(AndroidAppArtifactName)
            .Executes(async cancellationToken =>
            {
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                switch (MobileAppFlavor)
                {
                    case MobileAppFlavor.Production when ApiBaseUrlProduction is null:
                        throw new InvalidOperationException("ApiBaseUrlProduction must be set for production flavor");
                    case MobileAppFlavor.Development when ApiBaseUrlDevelopment is null:
                        throw new InvalidOperationException("ApiBaseUrlDevelopment must be set for development flavor");
                }

                var mobileBuildId = BuildTimestamp / 10;
                var outputsDir = MobileAppDirectory / "build" / "app" / "outputs";
                var debugSymbolsPath = outputsDir / AndroidDartSymbolsName;

                var outputDirectoryName = MobileAppFlavor switch
                {
                    MobileAppFlavor.Development => "developmentDebug",
                    MobileAppFlavor.Production => "productionRelease",
                    _ => throw new ArgumentOutOfRangeException(),
                };

                var outputDirectory = MobileAppFlavor is MobileAppFlavor.Development
                    ? outputsDir / "flutter-apk"
                    : outputsDir / "bundle" / outputDirectoryName;

                var appExtension = MobileAppFlavor is MobileAppFlavor.Development
                    ? "apk"
                    : "aab";

                var outputFileName = MobileAppFlavor is MobileAppFlavor.Development
                    ? $"app-development-debug.{appExtension}"
                    : $"app-production-release.{appExtension}";

                var outputFilePath = outputDirectory / outputFileName;

                var flavorName = MobileAppFlavor
                    .ToString()
                    .ToLowerInvariant();

                RootedFileSystem.CleanOrCreateDirectories([
                    outputsDir,
                    RootedFileSystem.AtomPublishDirectory / AndroidAppArtifactName,
                    RootedFileSystem.AtomPublishDirectory / AndroidAppArtifactName / AndroidDartSymbolsName,
                    debugSymbolsPath,
                ]);

                Dictionary<string, string?> buildVariables = [];
                RootedPath? keystorePath = null;

                try
                {
                    if (MobileAppFlavor is not MobileAppFlavor.Development)
                    {
                        keystorePath = await WriteKeystoreFile(AndroidKeystoreContents,
                            cancellationToken: cancellationToken);

                        buildVariables["SIGNING_RELEASE_KEY_ALIAS"] = AndroidKeystoreAlias;
                        buildVariables["SIGNING_RELEASE_KEY_PASSWORD"] = AndroidKeystorePassword;
                        buildVariables["SIGNING_RELEASE_STORE_FILE"] = keystorePath.ToString();
                        buildVariables["SIGNING_RELEASE_STORE_PASSWORD"] = AndroidKeystorePassword;
                    }

                    // One argument per item: FlutterBuild quotes each one, so none may hold several
                    await FlutterBuild([
                            "build",
                            .. MobileAppFlavor is MobileAppFlavor.Development
                                ? (string[])["apk"]
                                : ["appbundle", "--obfuscate", $"--split-debug-info={debugSymbolsPath}"],
                            "--flavor",
                            flavorName,
                            "--target",
                            "lib/main.dart",
                            .. GetDartDefines(),
                            "--build-name",
                            $"{BuildVersion.Major}.{BuildVersion.Minor}.{BuildVersion.Patch}",
                            "--build-number",
                            $"{mobileBuildId}",
                            .. MobileAppFlavor is MobileAppFlavor.Development
                                ? (string[])["--debug"]
                                : [],
                            .. LogOptions.IsVerboseEnabled
                                ? (string[])["--verbose"]
                                : [],
                        ],
                        MobileAppDirectory,
                        buildVariables,
                        cancellationToken);

                    var packagePublishFile = RootedFileSystem.AtomPublishDirectory /
                                             AndroidAppArtifactName /
                                             $"com.invex.passel-{flavorName}.{appExtension}";

                    RootedFileSystem.File.Move(outputFilePath, packagePublishFile);

                    if (MobileAppFlavor is MobileAppFlavor.Production)
                        AddSymbolsToPublishFolder(
                            RootedFileSystem.AtomPublishDirectory / AndroidAppArtifactName / AndroidDartSymbolsName,
                            debugSymbolsPath,
                            RootedFileSystem.AtomPublishDirectory / AndroidAppArtifactName,
                            outputsDir / "mapping" / outputDirectoryName);
                }
                finally
                {
                    if (keystorePath != null)
                        RootedFileSystem.File.Delete(keystorePath);
                }
            });

    /// <summary>
    ///     Compile-time configuration for the app. A local <c>src/mobile_app/.env</c>, if present, supplies defaults;
    ///     the explicit defines below take precedence over it.
    /// </summary>
    private List<string> GetDartDefines()
    {
        var apiBaseUrl = MobileAppFlavor is MobileAppFlavor.Production
            ? ApiBaseUrlProduction
            : ApiBaseUrlDevelopment;

        List<string> defines = [];

        if ((MobileAppDirectory / ".env").FileExists)
            defines.Add("--dart-define-from-file=.env");

        defines.Add($"--dart-define=API_BASE_URL={apiBaseUrl}");

        AddClientId("SERVER_CLIENT_ID", nameof(ServerClientId), ServerClientId);
        AddClientId("APP_CLIENT_ID", nameof(MobileAppClientId), MobileAppClientId);

        return defines;

        void AddClientId(string defineName, string paramName, string? value)
        {
            if (value?.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) is true)
            {
                defines.Add($"--dart-define={defineName}={value}");

                return;
            }

            // Guard against passing a placeholder that would silently break Google sign-in
            Logger.LogWarning(
                "{Param} is not a Google OAuth client id ('{Value}'); {Define} must come from src/mobile_app/.env",
                paramName,
                value,
                defineName);
        }
    }
}
