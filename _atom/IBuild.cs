namespace Atom;

[BuildDefinition]
[GenerateEntryPoint]
[GenerateSolutionModel]
internal interface IBuild : IWorkflowBuildDefinition,
    IGithubWorkflows,
    IAzureKeyVault,
    IBuildWebApi,
    IDeployWebApi,
    IBuildMobileAppAndroid,
    IBuildChromeExtension,
    IBuildWebApp,
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
        // Every pull request: all three test suites, the Azure deployment's Bicep and a docs build, nothing published
        new("Validate")
        {
            Triggers = [WorkflowTriggers.Manual, WorkflowTriggers.PullIntoMain],
            Targets =
            [
                new(nameof(TestWebApi)),
                new(nameof(TestChromeExtension)),
                new(nameof(TestMobileApp)),
                // The Bicep of the Azure deployment, kept with the run for review; nothing signs in to Azure
                new(nameof(PublishAzureInfrastructure)),
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

        // Manual only, behind the production environment, so nothing deploys until the owner has set Azure up
        // (docs/guides/deploying.md). Signs in to Azure with OIDC: no stored Azure secret
        new("Deploy")
        {
            Triggers = [WorkflowTriggers.Manual],
            Targets =
            [
                new(nameof(SetupBuildInfo)),
                new(nameof(DeployWebApi))
                {
                    Options =
                    [
                        BuildOptions.Deploy.ToEnvironment(DeploymentEnvironment),
                        BuildOptions.Github.TokenPermissions.Set(new Permissions.Exact(new()
                        {
                            Contents = PermissionsLevel.Read,
                            IdTokens = PermissionsLevel.Write,
                        })),
                        AzureLogin,
                        BuildOptions.Inject.Param(nameof(AzureSubscriptionId),
                            TextExpressions.Github.VarsVar("AZURE_SUBSCRIPTION_ID")),
                        BuildOptions.Inject.Param(nameof(AzureResourceGroup),
                            TextExpressions.Github.VarsVar("AZURE_RESOURCE_GROUP")),
                        BuildOptions.Inject.Param(nameof(AzureLocation), TextExpressions.Github.VarsVar("AZURE_LOCATION")),
                        BuildOptions.Inject.Param(nameof(ServerClientId),
                            TextExpressions.Github.VarsVar("GOOGLE_WEB_CLIENT_ID")),
                        BuildOptions.Inject.Param(nameof(ChromeExtensionClientId),
                            TextExpressions.Github.VarsVar("CHROME_EXTENSION_CLIENT_ID")),
                        BuildOptions.Inject.Param(nameof(ApiDomain), TextExpressions.Github.VarsVar("API_DOMAIN")),
                        BuildOptions.Inject.Param(nameof(ApiCertificateName),
                            TextExpressions.Github.VarsVar("API_CERTIFICATE_NAME")),
                        // An Aspire parameter the AppHost reads itself, so it never becomes a build parameter
                        BuildOptions.Inject.EnvironmentVariable(EncodingAlphabetVariable,
                            TextExpressions.Github.SecretsSecret("ENCODING_ALPHABET")),
                    ],
                },
            ],
            Types = [WorkflowTypes.Github.Action],
            // Deployments queue rather than cancel each other: a cancelled one could stop between two Azure steps
            Options = [BuildOptions.Github.Concurrency.Set("deploy-webapi", (bool?)false)],
        },

        // Weekly dependency updates for every package manager in the repository
        WorkflowPresets.Github.Dependabot(new()
        {
            Version = 2,
            Updates =
            [
                DependabotUpdate(WorkflowLabels.Github.Dependabot.NugetEcosystem, "/"),
                DependabotUpdate("npm", "/src"),
                DependabotUpdate("pub", "/src/mobile_app"),
                DependabotUpdate("github-actions", "/"),
            ],
        }),
    ];

    /// <summary>
    ///     The GitHub environment the Deploy workflow runs in. Its protection rules (required reviewers, the branches
    ///     allowed to deploy) and its variables and secrets are configured on GitHub.
    /// </summary>
    const string DeploymentEnvironment = "production";

    /// <summary>
    ///     Signs the job in to Azure with the workflow's OIDC token, exchanged for the deployment identity's token
    ///     through its federated credential. The Azure CLI then holds the session that aspire deploy uses.
    /// </summary>
    static readonly IBuildOption AzureLogin = new GithubCustomAdditionalStep
    {
        Step = new Step.UsesStep
        {
            Name = "Azure login",
            Uses = "azure/login@v3",
            With = new Dictionary<string, TextExpressionCollection>
            {
                ["client-id"] = "${{ vars.AZURE_CLIENT_ID }}",
                ["tenant-id"] = "${{ vars.AZURE_TENANT_ID }}",
                ["subscription-id"] = "${{ vars.AZURE_SUBSCRIPTION_ID }}",
            },
        },
        Order = -1,
    };

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
