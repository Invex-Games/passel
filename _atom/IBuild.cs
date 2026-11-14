namespace Atom;

[BuildDefinition]
[GenerateEntryPoint]
[GenerateSolutionModel]
internal interface IBuild : IWorkflowBuildDefinition,
    IGitVersion,
    IDotnetToolInstallHelper,
    IGithubWorkflows,
    IAzureKeyVault,
    IBuildWebApi,
    IDeployWebApi,
    IBuildMobileAppAndroid,
    IBuildChromeExtension,
    IBuildWebApp,
    IPublishImages,
    IPublishDeployBundle,
    IDeployCompose,
    IValidateCompose,
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

        // Versions and build ids come from GitVersion (GitVersion.yml), so every commit on main gets its own version
        BuildOptions.GitVersion.ProvideBuildVersion,
        BuildOptions.GitVersion.ProvideBuildId,

        // Runners get the .NET SDK that global.json asks for
        BuildOptions.Steps.SetupDotnet.Dotnet100X(),
    ];

    // The files under .github/ are generated from these definitions: run `dotnet atom Gen` after changing them.
    // In headless mode (CI) a build fails early if the committed files are out of date.
    IReadOnlyList<WorkflowDefinition> IWorkflowBuildDefinition.Workflows =>
    [
        // Every pull request: all three test suites, the Azure deployment's Bicep, the compose deployment's files and
        // scripts and a docs build, nothing published
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
                new(nameof(ValidateCompose)),
                new(nameof(BuildDocs))
                {
                    Options = [BuildOptions.Target.SuppressArtifactPublishing],
                },
            ],
            Types = [WorkflowTypes.Github.Action],
            Options = [CancelSupersededWorkflowRuns],
        },

        // Every push to main: tests, release builds of the API, the extension and the web app, the images of the
        // self-hosted deployment (pushed to GHCR from main only) and the docs site
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
                // The site holds no environment's values; the web image is configured when its container starts
                new(nameof(BuildWebApp)),
                new(nameof(PublishImages))
                {
                    Options =
                    [
                        BuildOptions.Github.TokenPermissions.Set(new Permissions.Exact(new()
                        {
                            Contents = PermissionsLevel.Read,
                            Packages = PermissionsLevel.Write,
                        })),
                        GhcrLogin,
                        // Still only on refs/heads/main: a manual run on another branch builds the images only
                        BuildOptions.Inject.Param(nameof(PushImages), "true"),
                    ],
                },
                new(nameof(PublishDeployBundle))
                {
                    Options =
                    [
                        BuildOptions.Github.TokenPermissions.Set(new Permissions.Exact(new()
                        {
                            Contents = PermissionsLevel.Read,
                            Packages = PermissionsLevel.Write,
                        })),
                        GhcrLogin,
                        BuildOptions.Inject.Param(nameof(PushImages), "true"),
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

        // Manual only: deploys a tag to one of the self-hosted stacks (ADR 0004), pre-prod or production, in the
        // GitHub environment of the same name, through the deploy gate that environment's SSH key is restricted to. A
        // blank tag deploys this ref's build; an older tag with migrate off rolls back (docs/guides/deploying.md)
        new("DeployCompose")
        {
            Triggers =
            [
                WorkflowTriggers.ManualWithInputs(
                    // Required with no default. GitHub's run dialog still preselects the first choice, so preprod
                    // comes first and production is always picked on purpose
                    new ManualChoiceInput("target",
                        "Stack to deploy to, and the GitHub environment the run uses",
                        true,
                        IDeployCompose.DeployTargets,
                        null),
                    new ManualStringInput("image-tag",
                        "Tag to deploy; blank = this ref; an older tag rolls back",
                        false,
                        null),
                    new ManualBoolInput("migrate",
                        "Run db-migrate before the API starts; off for a rollback",
                        false,
                        true)),
            ],
            Targets =
            [
                new(nameof(SetupBuildInfo)),
                new(nameof(DeployCompose))
                {
                    Options =
                    [
                        // The target names the environment, so its protection rules apply and the variables and the
                        // secret below are that stack's: each has its own DEPLOY_HOST, DEPLOY_USER, DEPLOY_KNOWN_HOSTS
                        // and DEPLOY_SSH_KEY. Atom writes the environment as given, so the expression is spelled out
                        // whole
                        BuildOptions.Deploy.ToEnvironment("${{ inputs.target }}"),
                        BuildOptions.Github.TokenPermissions.Set(new Permissions.Exact(new()
                        {
                            Contents = PermissionsLevel.Read,
                            Packages = PermissionsLevel.Read,
                        })),
                        // The job checks that the tag's three images exist before it reaches the server
                        GhcrLogin,
                        BuildOptions.Inject.Param(nameof(DeployTarget), TextExpressions.Github.InputsInput("target")),
                        BuildOptions.Inject.Param(nameof(DeployImageTag), TextExpressions.Github.InputsInput("image-tag")),
                        BuildOptions.Inject.Param(nameof(Migrate), TextExpressions.Github.InputsInput("migrate")),
                        BuildOptions.Inject.Param(nameof(DeployHost), TextExpressions.Github.VarsVar("DEPLOY_HOST")),
                        BuildOptions.Inject.Param(nameof(DeployUser), TextExpressions.Github.VarsVar("DEPLOY_USER")),
                        // Raw environment variables rather than secrets of the build, so no Key Vault lookup comes with
                        // them. The job's own token reads the images and expires with the job
                        BuildOptions.Inject.EnvironmentVariable(IDeployCompose.SshKeyVariable,
                            TextExpressions.Github.SecretsSecret("DEPLOY_SSH_KEY")),
                        BuildOptions.Inject.EnvironmentVariable(IDeployCompose.KnownHostsVariable,
                            TextExpressions.Github.VarsVar("DEPLOY_KNOWN_HOSTS")),
                        BuildOptions.Inject.EnvironmentVariable(IDeployCompose.PullTokenVariable,
                            TextExpressions.Github.SecretsGithubToken),
                    ],
                },
            ],
            Types = [WorkflowTypes.Github.Action],
            // Deployments to one stack queue rather than cancel each other, since a cancelled one could stop between
            // two compose steps; the two stacks deploy independently
            Options = [BuildOptions.Github.Concurrency.Set("deploy-compose-${{ inputs.target }}", (bool?)false)],
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
    ///     Signs Docker in to GHCR with the job's own token, which the job's packages: write permission lets push the
    ///     repository's images. The .NET SDK's container push reads the same Docker credentials.
    /// </summary>
    static readonly IBuildOption GhcrLogin = new GithubCustomAdditionalStep
    {
        Step = new Step.UsesStep
        {
            Name = "GHCR login",
            Uses = "docker/login-action@v3",
            With = new Dictionary<string, TextExpressionCollection>
            {
                ["registry"] = "ghcr.io",
                ["username"] = "${{ github.actor }}",
                ["password"] = "${{ secrets.GITHUB_TOKEN }}",
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
