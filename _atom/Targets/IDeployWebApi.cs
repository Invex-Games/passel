namespace Atom.Targets;

/// <summary>
///     Deploys the API to Azure with <c>aspire deploy</c>, from the Azure model in the AppHost (AzureHosting.cs): it
///     provisions or updates the infrastructure, builds the images with the .NET SDK's container support, runs the
///     database migration job and then rolls out the API. Needs a signed-in Azure CLI (the workflow's OIDC login);
///     see docs/guides/deploying.md and ADR 0003.
/// </summary>
internal interface IDeployWebApi : IBuildChromeExtension, ISetupBuildInfo
{
    const string AzureInfrastructureArtifactName = "AzureInfrastructure";

    [ParamDefinition("azure-subscription-id", "The Azure subscription the API is deployed to")]
    string? AzureSubscriptionId => GetParam(() => AzureSubscriptionId);

    [ParamDefinition("azure-resource-group", "The Azure resource group the API is deployed to")]
    string? AzureResourceGroup => GetParam(() => AzureResourceGroup);

    [ParamDefinition("azure-location", "The Azure region of the API's resources, for example australiaeast")]
    string? AzureLocation => GetParam(() => AzureLocation);

    [ParamDefinition("api-domain", "The API's custom domain, once its DNS records exist; empty for none")]
    string? ApiDomain => GetParam(() => ApiDomain);

    [ParamDefinition("api-certificate-name",
        "The managed certificate bound to the API's custom domain; empty for the first deployment with the domain")]
    string? ApiCertificateName => GetParam(() => ApiCertificateName);

    private RootedPath AppHostProject => Projects.Invex_Passel_AppHost.Path(RootedFileSystem);

    Target DeployWebApi =>
        t => t
            .DescribedAs("Deploys the API to Azure Container Apps with aspire deploy, migrating the database first")
            .RequiresParam(nameof(AzureSubscriptionId))
            .RequiresParam(nameof(AzureResourceGroup))
            .RequiresParam(nameof(AzureLocation))
            .RequiresParam(nameof(ServerClientId))
            .RequiresParam(nameof(ChromeExtensionClientId))
            .UsesParam(nameof(ApiDomain))
            .UsesParam(nameof(ApiCertificateName))
            .ConsumesVariable(nameof(SetupBuildInfo), nameof(BuildVersion))
            .ProducesArtifact(AzureInfrastructureArtifactName)
            .Executes(async cancellationToken =>
            {
                // A placeholder would deploy an API that rejects every token
                RequireGoogleClientId(nameof(ServerClientId), ServerClientId);
                RequireGoogleClientId(nameof(ChromeExtensionClientId), ChromeExtensionClientId);

                // The Sqids alphabet has no default here: deploying without it would change every public id
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EncodingAlphabetVariable)))
                    throw new StepFailedException(
                        $"{EncodingAlphabetVariable} must be set (the ENCODING_ALPHABET secret of the workflow's environment)");

                // Docker tags allow neither '+' nor '/', which a SemVer's build metadata or a branch label can carry
                var imageTag = BuildVersion
                    .ToString()
                    .Replace('+', '-')
                    .Replace('/', '-');

                Dictionary<string, string?> deployment = new()
                {
                    ["Azure__SubscriptionId"] = AzureSubscriptionId,
                    ["Azure__ResourceGroup"] = AzureResourceGroup,
                    ["Azure__Location"] = AzureLocation,
                    // The session azure/login left in the Azure CLI, and the resource group the owner created: the
                    // deployment identity has rights in that group only
                    ["Azure__CredentialSource"] = "AzureCli",
                    ["Azure__AllowResourceGroupCreation"] = "false",
                    ["Parameters__google-client-id"] = ServerClientId,
                    ["Parameters__google-extension-client-id"] = ChromeExtensionClientId,
                    ["Deployment__ImageTag"] = imageTag,
                    ["Deployment__ApiDomain"] = ApiDomain ?? string.Empty,
                    ["Parameters__api-certificate-name"] = ApiCertificateName ?? string.Empty,
                };

                // The Bicep for the record, attached to the run: the same model the deployment provisions
                await PublishAzureInfrastructureTo(deployment, cancellationToken);

                Logger.LogInformation("Deploying the API as {ImageTag} to {ResourceGroup}", imageTag, AzureResourceGroup);

                await RunAspire(["deploy", "--environment", "Production"], deployment, cancellationToken);
            });

    /// <summary>
    ///     Writes the Bicep of the Azure deployment, without signing in to Azure: run on every pull request, so a
    ///     change that breaks the Azure model fails before anyone deploys it, and the files can be read in review.
    /// </summary>
    Target PublishAzureInfrastructure =>
        t => t
            .DescribedAs("Writes the Bicep that DeployWebApi provisions, from the AppHost, without contacting Azure")
            .ProducesArtifact(AzureInfrastructureArtifactName)
            .Executes(cancellationToken => PublishAzureInfrastructureTo(new(), cancellationToken));

    private async Task PublishAzureInfrastructureTo(
        Dictionary<string, string?> environmentVariables,
        CancellationToken cancellationToken)
    {
        var infrastructureDirectory = RootedFileSystem.AtomPublishDirectory / AzureInfrastructureArtifactName;
        RootedFileSystem.CleanOrCreateDirectory(infrastructureDirectory);

        await RunAspire(["publish", "--output-path", infrastructureDirectory.ToString()],
            environmentVariables,
            cancellationToken);
    }

    private async Task RunAspire(
        string[] command,
        Dictionary<string, string?> environmentVariables,
        CancellationToken cancellationToken)
    {
        // The Aspire CLI is pinned in the tool manifest
        await ProcessRunner.RunAsync(new("dotnet", "tool restore"), cancellationToken);

        await ProcessRunner.RunAsync(new("dotnet",
            [
                "aspire",
                .. command,
                "--apphost",
                AppHostProject.ToString(),
                "--non-interactive",
            ])
            {
                EnvironmentVariables = environmentVariables,
                OutputLogLevel = LogLevel.Information,
            },
            cancellationToken);
    }

    /// <summary>
    ///     The AppHost reads the alphabet straight from the environment (an Aspire parameter), so the secret never
    ///     passes through a build parameter.
    /// </summary>
    const string EncodingAlphabetVariable = "Parameters__encoding-alphabet";

    private static void RequireGoogleClientId(string paramName, string? value)
    {
        if (value?.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) is not true)
            throw new StepFailedException($"{paramName} is not a Google OAuth client id ('{value}')");
    }
}
