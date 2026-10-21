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
            .DescribedAs("Runs the Web API tests")
            .Executes(async cancellationToken => await ProcessRunner.RunAsync(new("dotnet",
                    ["test", "--project", Projects.Invex_Passel_WebApi_Tests.Path(RootedFileSystem)]),
                cancellationToken));

    Target TestChromeExtension =>
        t => t
            .DescribedAs("Type-checks and runs the Chrome Extension tests")
            .Executes(async cancellationToken =>
            {
                var npmPath = await RestoreChromeExtensionPackages(cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "run typecheck")
                    {
                        WorkingDirectory = ChromeExtensionDirectory,
                    },
                    cancellationToken);

                await ProcessRunner.RunAsync(new(npmPath, "test")
                    {
                        WorkingDirectory = ChromeExtensionDirectory,
                    },
                    cancellationToken);
            });

    Target TestMobileApp =>
        t => t
            .DescribedAs("Analyzes and runs the mobile app tests")
            .Executes(async cancellationToken =>
            {
                // Uses whichever Flutter is installed; unlike FlutterBuild, never switches the SDK to flutter-version
                var flutterPath = await GetFlutterCommand(cancellationToken);

                await ProcessRunner.RunAsync(new(flutterPath, "analyze")
                    {
                        WorkingDirectory = MobileAppDirectory,
                    },
                    cancellationToken);

                await ProcessRunner.RunAsync(new(flutterPath, "test")
                    {
                        WorkingDirectory = MobileAppDirectory,
                    },
                    cancellationToken);
            });
}
