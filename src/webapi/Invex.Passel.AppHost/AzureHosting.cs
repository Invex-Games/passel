// Image push options are experimental in Aspire 13; they give the images the build's version as their tag
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.PostgreSql;
using Microsoft.Extensions.Configuration;

/// <summary>
///     The Azure deployment of the API (ADR 0003): Azure Container Apps for the API, Azure Database for PostgreSQL
///     flexible server, Key Vault for the one secret, and Application Insights over a Log Analytics workspace. Only
///     added when the AppHost is published or deployed, so the local run needs nothing from Azure. `aspire deploy`
///     provisions it all, builds the API's image with the .NET SDK's container support and rolls it out. See
///     docs/guides/deploying.md.
/// </summary>
internal static class AzureHosting
{
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

    public static void AddAzureHosting(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> webApi,
        IResourceBuilder<AzurePostgresFlexibleServerResource> postgres,
        IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> database)
    {
        // Optional deployment settings, from configuration (Deployment__ImageTag and Deployment__ApiDomain in the
        // workflow): the tag the image is pushed with, GitVersion's version in CI, and the custom domain once it
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
            // No password: the API signs in as its identity with an Entra token (Aspire.Azure.Npgsql). Referencing the
            // server makes the identity one of its Microsoft Entra administrators, which the API needs while it still
            // applies the schema at startup
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

        if (imageTag is { Length: > 0 })
            webApi.WithImagePushOptions(context => context.Options.RemoteImageTag = imageTag);
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

}
