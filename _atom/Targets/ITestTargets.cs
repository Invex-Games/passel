namespace Atom.Targets;

internal interface ITestTargets : IChromeExtensionHelper, IFlutterHelper, IMobileAppHelper
{
    Target Test =>
        t => t
            .DescribedAs("Runs the Web API, Chrome Extension and mobile app tests")
            .DependsOn(nameof(TestWebApi))
            .DependsOn(nameof(TestChromeExtension))
            .DependsOn(nameof(TestMobileApp));

    // Runs dotnet test directly: DotnetTestAndStage passes --output, which Microsoft.Testing.Platform mode (see
    // global.json) parses as a verbosity
    Target TestWebApi =>
        t => t
            .DescribedAs("Runs the Web API unit tests, then its integration tests, which need Docker")
            .Executes(async cancellationToken =>
            {
                // The unit tests first: they are quick and need nothing. The integration tests then start PostgreSQL
                // in a container (GitHub-hosted runners have Docker)
                await RunTests(Projects.Invex_Passel_WebApi_Tests.Path(RootedFileSystem));
                await RunTests(Projects.Invex_Passel_WebApi_IntegrationTests.Path(RootedFileSystem));

                Task RunTests(string project) =>
                    ProcessRunner.RunAsync(new("dotnet", ["test", "--project", project]), cancellationToken);
            });

    Target TestChromeExtension =>
        t => t
            .DescribedAs(
                "Type-checks and runs the tests of the npm workspace (the shared packages, the Chrome Extension and the web app) with coverage, keeping the lcov reports")
            .ProducesArtifact(ChromeExtensionCoverageArtifactName)
            .Executes(async cancellationToken =>
            {
                var npmPath = await RestoreChromeExtensionPackages(cancellationToken);

                // The workspace scripts run every member: @passel/core, @passel/ui, the extension and the web app
                await ProcessRunner.RunAsync(new(npmPath, "run typecheck")
                    {
                        WorkingDirectory = WebWorkspaceDirectory,
                    },
                    cancellationToken);

                // Each member with tests writes coverage/lcov.info (line and branch coverage of its src/) next to them
                await ProcessRunner.RunAsync(new(npmPath, "run test:coverage")
                    {
                        WorkingDirectory = WebWorkspaceDirectory,
                    },
                    cancellationToken);

                var publishDirectory = RootedFileSystem.AtomPublishDirectory / ChromeExtensionCoverageArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(publishDirectory);

                RootedFileSystem.File.Copy(ChromeExtensionDirectory / "coverage" / "lcov.info",
                    publishDirectory / "chrome-extension.lcov.info");

                RootedFileSystem.File.Copy(WebWorkspaceDirectory / "packages" / "core" / "coverage" / "lcov.info",
                    publishDirectory / "core.lcov.info");

                RootedFileSystem.File.Copy(WebWorkspaceDirectory / "web" / "coverage" / "lcov.info",
                    publishDirectory / "web.lcov.info");
            });

    Target TestMobileApp =>
        t => t
            .DescribedAs("Analyzes and runs the mobile app tests with coverage, keeping the lcov report")
            .ProducesArtifact(MobileAppCoverageArtifactName)
            .Executes(async cancellationToken =>
            {
                // Uses whichever Flutter is installed and never switches its version, unlike FlutterBuild; a machine
                // without Flutter (a CI runner) gets the pinned version installed into .flutter/
                var flutterPath = await GetOrInstallFlutterCommand(cancellationToken);

                await ProcessRunner.RunAsync(new(flutterPath, "analyze")
                    {
                        WorkingDirectory = MobileAppDirectory,
                    },
                    cancellationToken);

                // Writes coverage/lcov.info (line coverage of lib/) next to the tests
                await ProcessRunner.RunAsync(new(flutterPath, "test --coverage")
                    {
                        WorkingDirectory = MobileAppDirectory,
                    },
                    cancellationToken);

                var publishDirectory = RootedFileSystem.AtomPublishDirectory / MobileAppCoverageArtifactName;

                RootedFileSystem.CleanOrCreateDirectory(publishDirectory);
                RootedFileSystem.File.Copy(MobileAppDirectory / "coverage" / "lcov.info", publishDirectory / "lcov.info");
            });

    /// <summary>
    ///     The mobile app's lcov report, attached to the workflow run so the coverage of a change can be read.
    /// </summary>
    const string MobileAppCoverageArtifactName = "MobileAppCoverage";

    /// <summary>
    ///     The lcov reports of the Chrome extension and of @passel/core, attached to the workflow run like the mobile
    ///     app's.
    /// </summary>
    const string ChromeExtensionCoverageArtifactName = "ChromeExtensionCoverage";
}
