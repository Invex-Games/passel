namespace Atom;

[BuildDefinition]
[GenerateEntryPoint]
[GenerateSolutionModel]
internal interface IBuild : IWorkflowBuildDefinition,
    IGithubWorkflows,
    IAzureKeyVault,
    IBuildWebApi,
    IBuildMobileAppAndroid,
    IBuildChromeExtension,
    ITestTargets,
    IDocTargets,
    ISetupDevEnvironment
{
    /// <summary>
    ///     A newer run of the same workflow on the same ref cancels the one still in progress.
    /// </summary>
    static readonly IBuildOption CancelSupersededWorkflowRuns =
        BuildOptions.Github.Concurrency.Set("${{ github.workflow }}-${{ github.ref }}", (bool?)true);

    IReadOnlyList<IBuildOption> IBuildDefinition.Options =>
    [
        BuildOptions.AzureKeyVault.UseAzureKeyVault,

        // Runners get the .NET SDK that global.json asks for
        BuildOptions.Steps.SetupDotnet.Dotnet100X(),
    ];

    // The files under .github/ are generated from these definitions: run `dotnet atom Gen` after changing them.
    // In headless mode (CI) a build fails early if the committed files are out of date.
    IReadOnlyList<WorkflowDefinition> IWorkflowBuildDefinition.Workflows =>
    [
        // Every pull request: all three test suites and a docs build, nothing published
        new("Validate")
        {
            Triggers = [WorkflowTriggers.Manual, WorkflowTriggers.PullIntoMain],
            Targets =
            [
                new(nameof(TestWebApi)),
                new(nameof(TestChromeExtension)),
                new(nameof(TestMobileApp)),
                new(nameof(BuildDocs))
                {
                    Options = [BuildOptions.Target.SuppressArtifactPublishing],
                },
            ],
            Types = [WorkflowTypes.Github.Action],
            Options = [CancelSupersededWorkflowRuns],
        },

        // Every push to main: tests, release builds of the API and the extension, and the docs site
        new("Build")
        {
            Triggers = [WorkflowTriggers.Manual, WorkflowTriggers.PushToMain],
            Targets =
            [
                new(nameof(SetupBuildInfo)),
                new(nameof(TestWebApi)),
                new(nameof(TestChromeExtension)),
                new(nameof(TestMobileApp)),
                new(nameof(BuildWebApi)),
                new(nameof(BuildChromeExtension))
                {
                    Options =
                    [
                        // A client id is public configuration, so it lives in a repository variable
                        BuildOptions.Inject.Param(nameof(ChromeExtensionClientId),
                            TextExpressions.Github.VarsVar("CHROME_EXTENSION_CLIENT_ID")),
                    ],
                },
                new(nameof(BuildDocs)),
                new(nameof(PublishDocs))
                {
                    Options =
                    [
                        BuildOptions.Inject.Secret(nameof(GithubToken)),
                        BuildOptions.Github.TokenPermissions.Set(new Permissions.Exact(new()
                        {
                            Contents = PermissionsLevel.Write,
                        })),
                    ],
                },
            ],
            Types = [WorkflowTypes.Github.Action],
            Options = [CancelSupersededWorkflowRuns],
        },

        // Weekly dependency updates for every package manager in the repository
        WorkflowPresets.Github.Dependabot(new()
        {
            Version = 2,
            Updates =
            [
                DependabotUpdate(WorkflowLabels.Github.Dependabot.NugetEcosystem, "/"),
                DependabotUpdate("npm", "/src/chrome-extension"),
                DependabotUpdate("pub", "/src/mobile_app"),
                DependabotUpdate("github-actions", "/"),
            ],
        }),
    ];

    /// <summary>
    ///     One grouped pull request per package manager, once a week, with a commit prefix that GitVersion ignores.
    /// </summary>
    private static DependabotUpdate DependabotUpdate(string ecosystem, string directory) =>
        new()
        {
            PackageEcosystem = ecosystem,
            Directory = directory,
            Schedule = new()
            {
                Interval = ScheduleInterval.Weekly,
                Day = ScheduleDay.Monday,
                Time = "06:00",
                Timezone = "Australia/Brisbane",
            },
            OpenPullRequestsLimit = 5,
            Groups = new Dictionary<string, DependabotGroup>
            {
                [$"{ecosystem}-dependencies"] = new DependabotGroup.FromPatterns
                {
                    Patterns = ["*"],
                },
            },
            CommitMessage = new()
            {
                Prefix = "build",
                PrefixDevelopment = "build",
                Include = CommitMessageInclude.Scope,
            },
        };
}
