// The deployment pipeline API (custom steps, image push options) is experimental in Aspire 13; the migration step needs it
#pragma warning disable ASPIREPIPELINES001, ASPIREPIPELINES003

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.PostgreSql;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using JobExecutionRunningState = Azure.ResourceManager.AppContainers.Models.JobExecutionRunningState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
///     The Azure deployment of the API (ADR 0003): Azure Container Apps for the API and for its database migration
///     job, Azure Database for PostgreSQL flexible server, Key Vault for the one secret, and Application Insights over
///     a Log Analytics workspace. Only added when the AppHost is published or deployed, so the local run needs nothing
///     from Azure. `aspire deploy` provisions it all, builds both images with the .NET SDK's container support, runs the
///     migration job, and only then rolls out the API. See docs/guides/deploying.md.
/// </summary>
internal static class AzureHosting
{
    /// <summary>
    ///     The migration job's resource name, which is also its name in Azure.
    /// </summary>
    private const string MigrationJobName = "passel-migrate";

    /// <summary>
    ///     The API's resource name, which is also its container app's name in Azure and the first label of its
    ///     default host name.
    /// </summary>
    private const string ApiName = "invex-passel-webapi";

    /// <summary>
    ///     The Host header of the health probes. <c>AllowedHosts</c> includes it, because a probe addresses the
    ///     replica directly and would otherwise be refused with a 400.
    /// </summary>
    private const string ProbeHost = "localhost";

    private const string MigrationStepName = "run-database-migration";

    /// <summary>
    ///     How long the deployment waits for the migration job, which itself gives up after
    ///     <see cref="MigrationTimeout" />.
    /// </summary>
    private static readonly TimeSpan MigrationWait = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan MigrationTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan MigrationPollInterval = TimeSpan.FromSeconds(10);

    public static void AddAzureHosting(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> webApi,
        IResourceBuilder<AzurePostgresFlexibleServerResource> postgres,
        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> database)
    {
        // Optional deployment settings, from configuration (Deployment__ImageTag and Deployment__ApiDomain in the
        // workflow): the tag the images are pushed with, GitVersion's version in CI, and the custom domain once it
        // has DNS records
        var imageTag = builder.Configuration["Deployment:ImageTag"];
        var apiDomain = builder.Configuration["Deployment:ApiDomain"];

        var logs = builder.AddAzureLogAnalyticsWorkspace("passel-logs");

        // Telemetry goes to Application Insights, so the environment's own Aspire dashboard is left out
        var environment = builder
            .AddAzureContainerAppEnvironment("passel-env")
            .WithAzureLogAnalyticsWorkspace(logs)
            .WithDashboard(false);

        var insights = builder
            .AddAzureApplicationInsights("passel-insights")
            .WithLogAnalyticsWorkspace(logs);

        // The Sqids alphabet is the API's one secret in production; the Google client secret is only used by the
        // development-only Scalar login, so it is never deployed
        var vault = builder.AddAzureKeyVault("passel-kv");

        var alphabet = vault.AddSecret("encoding-alphabet-secret",
            "encoding-alphabet",
            builder
                .AddParameter("encoding-alphabet", secret: true)
                .WithDescription("The Sqids alphabet for public ids. Must never change once ids are in use."));

        postgres.ConfigureInfrastructure(ConfigureDatabaseServer);

        // Aspire makes every compute resource that references the server one of its Microsoft Entra administrators.
        // Only the migration job should be: the API's identity gets a plain role that the job grants DML to
        RemoveDefaultRoleAssignments(postgres.Resource);

        var apiIdentity = builder.AddAzureUserAssignedIdentity("passel-api-identity");

        // Two deployments bind a custom domain: the first adds it without a certificate, the second with the managed
        // certificate created in between (docs/guides/deploying.md)
        var customDomain = apiDomain is { Length: > 0 }
            ? (builder.AddParameter("api-domain", apiDomain),
                builder
                    .AddParameter("api-certificate-name")
                    .WithDescription(
                        "The managed certificate for the API domain; empty for the first deployment with the domain."))
            : ((IResourceBuilder<ParameterResource>, IResourceBuilder<ParameterResource>)?)null;

        webApi
            .WithAzureUserAssignedIdentity(apiIdentity)
            .WithReference(insights)
            // No password: the API signs in as its identity's role with an Entra token (Aspire.Azure.Npgsql)
            .WithEnvironment("ConnectionStrings__passel-db",
                ReferenceExpression.Create(
                    $"Host={postgres.Resource.HostName};Database={database.Resource.DatabaseName};Username={apiIdentity.Resource.PrincipalName};Ssl Mode=Require"))
            .WithEnvironment("Encoding__Alphabet", alphabet.Resource)
            .WithEnvironment("AllowedHosts", AllowedHosts(environment, apiDomain))
            .WithExternalHttpEndpoints()
            .PublishAsAzureContainerApp((_, app) =>
            {
                // One replica: the rate limits and the event-stream caps are counted per instance, and the HTTP scale
                // rule would count every open event stream as load
                app.Template.Scale.MinReplicas = 1;
                app.Template.Scale.MaxReplicas = 1;

                var container = app.Template.Containers.Single().Value!;

                container.Resources = new AppContainerResources
                {
                    Cpu = 0.5,
                    Memory = "1Gi",
                };

                // Ready when every health check passes, the database included; alive while the process answers
                container.Probes.Add(HttpProbe(app, ContainerAppProbeType.Readiness, "/health"));
                container.Probes.Add(HttpProbe(app, ContainerAppProbeType.Liveness, "/alive"));

                if (customDomain is (var domain, var certificate))
                    app.ConfigureCustomDomain(domain, certificate);
            });

        // The same image with a command: applies the schema as a database administrator and grants the API's role
        // its privileges (docs/components/web-api.md)
        var migration = builder
            .AddProject<Projects.Invex_Passel_WebApi>(MigrationJobName, launchProfileName: null)
            .WithArgs("db-migrate")
            .WithReference(database)
            .WithEnvironment("Database__ApiRole", apiIdentity.Resource.PrincipalName)
            .WithEnvironment("Database__ApiRoleObjectId", apiIdentity.Resource.PrincipalId)
            .WithAnnotation(new RoleAssignmentAnnotation(postgres.Resource, new HashSet<RoleDefinition>()))
            .PublishAsAzureContainerAppJob((_, job) =>
            {
                job.Configuration.ReplicaTimeout = (int)MigrationTimeout.TotalSeconds;
                job.Configuration.ReplicaRetryLimit = 0;
            });

        if (imageTag is { Length: > 0 })
        {
            webApi.WithImagePushOptions(context => context.Options.RemoteImageTag = imageTag);
            migration.WithImagePushOptions(context => context.Options.RemoteImageTag = imageTag);
        }

        // After the job is updated to the new image and before the API's container app is: a revision of the API
        // never starts on a schema it does not expect, and a failed migration stops the deployment with the running
        // revision untouched. The container app steps only exist when deploying (not for `aspire publish`), so the step
        // is wired in once the pipeline knows its steps
        builder.Pipeline.AddStep(MigrationStepName, RunMigrationJobAsync);
        builder.Pipeline.AddPipelineConfiguration(OrderMigrationStep);
    }

    private static Task OrderMigrationStep(PipelineConfigurationContext context)
    {
        var steps = context.Steps.ToDictionary(step => step.Name);

        if (steps.TryGetValue($"provision-{MigrationJobName}-containerapp", out var job) &&
            steps.TryGetValue($"provision-{ApiName}-containerapp", out var api))
        {
            var migration = steps[MigrationStepName];

            migration.DependsOn(job);
            api.DependsOn(migration);
        }

        return Task.CompletedTask;
    }

    private static void ConfigureDatabaseServer(AzureResourceInfrastructure infrastructure)
    {
        var server = infrastructure
            .GetProvisionableResources()
            .OfType<PostgreSqlFlexibleServer>()
            .Single();

        // The version the AppHost and the tests run locally
        server.Version = new StringLiteralExpression("18");

        // Burstable B1ms with 32 GB is Aspire's default and plenty for now. Point-in-time restore covers the
        // retention window; it is set here rather than left to a default that could change
        server.Backup = new PostgreSqlFlexibleServerBackupProperties
        {
            BackupRetentionDays = 14,
            GeoRedundantBackup = PostgreSqlFlexibleServerGeoRedundantBackupEnum.Disabled,
        };

        server.Storage = new PostgreSqlFlexibleServerStorage
        {
            StorageSizeInGB = 32,
            AutoGrow = StorageAutoGrow.Enabled,
        };
    }

    private static void RemoveDefaultRoleAssignments(IResource resource)
    {
        foreach (var annotation in resource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>().ToList())
            resource.Annotations.Remove(annotation);
    }

    /// <summary>
    ///     The container app's default host name, the probes' host and the custom domain when there is one.
    /// </summary>
    private static ReferenceExpression AllowedHosts(
        IResourceBuilder<AzureContainerAppEnvironmentResource> environment,
        string? apiDomain)
    {
        var hosts = new ReferenceExpressionBuilder();

        hosts.AppendLiteral($"{ApiName}.");
        hosts.AppendFormatted(environment.GetOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"));
        hosts.AppendLiteral($";{ProbeHost}");

        if (apiDomain is { Length: > 0 })
            hosts.AppendLiteral($";{apiDomain}");

        return hosts.Build();
    }

    private static ContainerAppProbe HttpProbe(ContainerApp app, ContainerAppProbeType type, string path) =>
        new()
        {
            ProbeType = type,
            HttpGet = new ContainerAppHttpRequestInfo
            {
                Path = path,
                Port = app.Configuration.Ingress.TargetPort,
                HttpHeaders =
                {
                    new ContainerAppHttpHeaderInfo
                    {
                        Name = "Host",
                        Value = ProbeHost,
                    },
                },
            },
            InitialDelaySeconds = 3,
            PeriodSeconds = 10,
            TimeoutSeconds = 5,
            FailureThreshold = 3,
        };

    /// <summary>
    ///     Starts an execution of the migration job and waits for it to finish. The job's logs are in the Log Analytics
    ///     workspace, and in the portal under the job's execution history.
    /// </summary>
    private static async Task RunMigrationJobAsync(PipelineStepContext context)
    {
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var subscriptionId = RequiredSetting(configuration, "Azure:SubscriptionId");
        var resourceGroup = RequiredSetting(configuration, "Azure:ResourceGroup");
        var credential = context.Services.GetRequiredService<ITokenCredentialProvider>().TokenCredential;

        var job = new ArmClient(credential, subscriptionId)
            .GetContainerAppJobResource(
                ContainerAppJobResource.CreateResourceIdentifier(subscriptionId, resourceGroup, MigrationJobName));

        var started = await job.StartAsync(WaitUntil.Completed, cancellationToken: context.CancellationToken);
        var executionName = started.Value.Name;

        context.Logger.LogInformation("Started {Job} execution {Execution}", MigrationJobName, executionName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(MigrationWait);

        while (true)
        {
            await Task.Delay(MigrationPollInterval, timeout.Token);

            var execution = await job.GetContainerAppJobExecutionAsync(executionName, timeout.Token);
            var status = execution.Value.Data.Status;

            if (status == JobExecutionRunningState.Succeeded)
            {
                context.Logger.LogInformation("{Job} execution {Execution} succeeded", MigrationJobName, executionName);

                return;
            }

            if (status == JobExecutionRunningState.Failed ||
                status == JobExecutionRunningState.Stopped ||
                status == JobExecutionRunningState.Degraded)
                throw new InvalidOperationException(
                    $"The database migration ({MigrationJobName} execution {executionName}) ended as {status}, so the API was not updated. Its logs are in the passel-logs workspace (ContainerAppConsoleLogs_CL).");
        }
    }

    private static string RequiredSetting(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} must be set (the {key.Replace(":", "__")} environment variable) to run the database migration.");
}
