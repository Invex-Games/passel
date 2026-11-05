# ADR 0003: Hosting the API on Azure

**Status:** superseded by [ADR 0004](0004-hosting-on-docker-compose.md), November 2026: Passel runs on Docker Compose on the owner's server instead, and the Azure code and the Deploy workflow stay in place unused. Before that it was proposed, November 2026. The owner still has to confirm the subscription, the region and the services below, then do the one-off setup at the end; until then the Deploy workflow has nothing to run against. **Decides:** roadmap packages 0.A2 (deploying the API) and 0.A3 (the production schema and startup), within the Azure choice of [ADR 0002](0002-product-decisions.md).

## Context

Nothing deployed the API ([Developer Experience](../evaluation/developer-experience.md), finding 1): no image, no infrastructure, no documented source for the production settings, and Marten created and changed the schema at startup with the API's own credentials, racing across replicas ([Security](../evaluation/security.md), finding 5). ADR 0002 chose Azure and left the compute and database services to 0.A2. The local loop had to stay as it was: `aspire run` with a PostgreSQL container, and tests that need nothing but Docker.

## Options

| Compute | For | Against |
|---|---|---|
| **Azure Container Apps**, consumption plan | Aspire's main Azure target, so `aspire deploy` provisions and deploys it from the AppHost; revisions that only take traffic once their readiness probe passes; jobs for one-off work such as a migration; managed identity and Key Vault references; an idle rate when nothing is happening | A replica kept running costs more than a basic App Service plan; the HTTP scaler counts every open event stream as load, so scaling has to be set by hand |
| Azure App Service (Linux, containers) | Simple, always on, B1 is cheap | Nothing like a Container Apps job for one-off work such as a database migration; staging slots need the Standard tier |
| AKS | Full control | A cluster to run and pay for, for one small API |
| A VM with Docker Compose | Cheapest | Patching, TLS, backups and deployment all by hand |

| Database | For | Against |
|---|---|---|
| **Azure Database for PostgreSQL flexible server** | Managed PostgreSQL 18 (what the AppHost and the tests run), automatic backups with point-in-time restore, Microsoft Entra authentication so no password exists, `LISTEN` and `NOTIFY` work | The smallest server is still a fixed monthly cost |
| PostgreSQL in a container app | Cheap | No backups or restore, and Container Apps storage is not meant for a database |

## Decision

**Compute: Azure Container Apps** on the consumption plan, one environment (`passel-env`) with its own Azure Container Registry and a Log Analytics workspace. The API runs as one container app with 0.5 vCPU and 1 GiB, and exactly one replica (minimum and maximum): the rate limits and the event-stream caps are counted per instance, and the HTTP scale rule would treat every open event stream as load. Scaling out comes with shared rate limiting. Readiness is `/health` (every check, the database included) and liveness is `/alive`, both sent with `Host: localhost`, which the API's `AllowedHosts` includes so host filtering doesn't refuse the probes. The environment's own Aspire dashboard is turned off; telemetry goes to Application Insights instead.

**Database: Azure Database for PostgreSQL flexible server**, Burstable B1ms, PostgreSQL 18, 32 GB with storage auto-grow, high availability off. Backups are kept for 14 days, set explicitly rather than left to Aspire's default of 7, and any point in that window can be restored to a new server. Geo-redundant backup is off. It can only be chosen when the server is created, so turning it on later means restoring to a new server; the owner should decide before the first deployment. Password authentication is disabled: everything signs in with Microsoft Entra tokens for its managed identity. The server is on a public endpoint whose firewall only admits Azure services (Aspire's default rule). A private endpoint is the next step when the cost is justified.

**Secrets: Key Vault**, holding the API's one production secret, the Sqids alphabet (`Encoding:Alphabet`), which the container app reads as a Key Vault reference through its identity. Nothing else needs a secret: the database has no password, the Google client ids are public, and the Google client secret is only used by the development-only Scalar login, so it is not deployed at all. The Application Insights connection string is an ingestion endpoint and key rather than a credential, and is set as a plain environment variable, as Aspire does.

**Telemetry: Application Insights**, workspace-based on the same Log Analytics workspace. `ServiceDefaults` sends logs, traces and metrics with the Azure Monitor OpenTelemetry distro whenever `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, and to the OTLP endpoint otherwise, as before. Npgsql's per-command logs are kept to warnings in Production (`appsettings.Production.json`), so the workspace doesn't receive every SQL statement. Telemetry must not hold what users send, because it outlives a delete: the workspace keeps it for its default of 30 days. Requests are traced by route and status with the query redacted, and SQL is traced as statements without parameter values. The link preview fetcher is the one component that requests a URL a user sent, so its requests are left out of the traces (`ExcludeFromTelemetry`), outgoing HTTP and DNS metrics drop the host and address dimensions, and the reason a link got no preview, which can name the host, is logged at Debug, below what Production keeps.

**Identities.** Every resource signs in as a user-assigned managed identity. The API has its own (`passel-api-identity`), which can read the Key Vault secret and log in to PostgreSQL as an ordinary role. The migration job has another, which Aspire makes a Microsoft Entra administrator of the server. The environment's identity pulls images from the registry.

**Infrastructure as code: generated by Aspire.** The Azure resources are modelled in the AppHost (`AzureHosting.cs`) with the Aspire Azure hosting integrations, and only when it is published or deployed. Locally, `AddAzurePostgresFlexibleServer(...).RunAsContainer(...)` gives the same `passel-pg` container as before, and the Azure-only resources are never added, so `aspire run` and the tests need nothing from Azure. No Bicep is written by hand or committed. `aspire publish` writes it from the model, every pull request runs that through `PublishAzureInfrastructure` and attaches the files to the run for review, and `aspire deploy` provisions it.

**Images: the .NET SDK's container support.** `aspire deploy` builds the API's image with `dotnet publish` (`PublishContainer`), tags it with GitVersion's version for the commit, pushes it to the environment's registry and points the container app at it.

**Pipeline: a manual Deploy workflow.** `DeployWebApi` runs only on `workflow_dispatch`, in the `production` GitHub environment, and signs in with `azure/login` through a federated credential (OIDC): no Azure secret is stored in GitHub. Deployments queue rather than cancel each other. Merging this changes nothing until the owner has done the setup below.

**Schema: applied by the deployment, never by the API.** Outside Development the store uses `AutoCreate.None`. The API never changes the schema, and its role can't: the migration job grants it DML only. A migration job (`passel-migrate`, the same image started as `db-migrate`) applies the schema as the Entra administrator, creates the DML-only role `passel_app`, and makes the API's identity a member of it. `aspire deploy` runs the job between updating it to the new image and updating the API's container app, through a step added to Aspire's deployment pipeline, so a migration that fails stops the deployment with the running revision untouched. The job runs inside the Container Apps environment, so neither the runner nor the deployment identity ever needs access to the database.

**`ApplyAllDatabaseChangesOnStartup`.** The roadmap listed it for production, so replicas don't race the schema check. With the migration in the pipeline, production has nothing to apply, so Development keeps it, which keeps the local loop and the tests unchanged. Production checks the schema at startup instead (`DatabaseSchemaCheck`): a replica that finds a table, column or function it needs missing fails to start, never becomes ready and never takes traffic. The race is gone too, because nothing applies the schema concurrently. Marten's `AssertDatabaseMatchesConfigurationOnStartup` was the first choice, but it refuses any difference, a newer release's extra index included. The migration runs before the new revision starts, so if the deployment failed after it, the previous revision would fail at its next restart and, with one replica, the API would be down until someone fixed it. The check accepts what it doesn't need and logs it.

**Migrations are add-only.** Plain `CreateOrUpdate` drops every column, index and foreign key the configuration no longer declares. Rolling back to an older release would then delete the columns a newer release added, and their data: soft-delete flags, for example, once the trash exists. `db-migrate` marks every table add-only (Weasel's `AddOnlyMigrations`) and logs what it leaves in place. Removing a column for good is a deliberate statement run by hand, which `db-patch` writes.

**Static code generation: not needed.** The evaluation asked for `TypeLoadMode.Static` and `codegen write` because Marten used to generate and compile code with Roslyn on first use. Marten 9, which the API runs on, removed runtime code generation entirely: document storage is fixed code, `codegen write` finds nothing to generate, and `TypeLoadMode` no longer exists on `StoreOptions`. Nothing is committed and nothing needs regenerating after a merge, a new document type included. A unit test fails if a package ever registers code that would need generating ahead of time (Wolverine, for example), at which point the build needs that step and a check for stale output.

## Cost

A ballpark in US dollars at East US list prices, before tax; the region chosen and the pricing calculator give the real figure.

| Resource | Monthly |
|---|---|
| Container Apps: one 0.5 vCPU / 1 GiB replica, always running, after the monthly free grant | About 10 when mostly idle, up to about 35 when busy all month |
| PostgreSQL flexible server: B1ms, 32 GB, backups up to the storage size included | About 16 |
| Container Registry: Basic | About 5 |
| Log Analytics and Application Insights: the first 5 GB a month free | 0 to a few |
| Key Vault and managed identities | Cents |
| **Total** | **About 30 to 60** |

Geo-redundant backup roughly doubles the backup storage, and high availability doubles the database compute.

## Consequences

- The API now signs in to PostgreSQL through `Aspire.Azure.Npgsql`. A connection string with a password (the AppHost, the tests) is used as it is; one without uses an Entra token for the managed identity.
- `Program.cs` runs JasperFx commands when the first argument names one (`db-migrate`, `db-assert`, `db-patch`), and the API otherwise. A schema change is applied by the next deployment's migration job before the new revision starts, while the old revision is still serving. Changes must therefore be backwards compatible with the running code: add first, remove in a later release.
- The Deploy workflow only deploys `main`, so a rollback is a revert merged to `main` and deployed. Its migration puts back the older Marten functions and leaves what the newer release added, which the older code ignores. Until then, the container app can be pointed at an earlier image by hand, because the older code starts on the newer schema ([Rolling back](../guides/deploying.md#rolling-back)).
- Columns and indexes nothing uses any more stay until someone drops them by hand.
- The AppHost uses two experimental Aspire APIs, the deployment pipeline step and the image push options (`ASPIREPIPELINES001`, `ASPIREPIPELINES003`). An Aspire upgrade may change them. The migration step finds the two provisioning steps it sits between by their container apps and their `provision-infra` tag, and fails the pipeline if it can't, so an upgrade that moves them breaks `aspire publish` on its pull request rather than deploying the API without its migration.
- Nothing here has been provisioned. The Bicep, the images, the migration and the API's startup against a DML-only role are verified locally (see [Deploying](../guides/deploying.md#what-was-verified-locally)); the Entra mapping of the API's role and the first `aspire deploy` can only be checked in Azure.

## One-off setup by the owner

Commands use the Azure CLI. `<sub>`, `<rg>`, `<region>` and the names are placeholders, and the repository is `Invex-Games/passel`. [Deploying](../guides/deploying.md) explains each step.

- [ ] Choose the subscription and region, confirm the services and cost above, decide on geo-redundant backup, and change this ADR's status to accepted.
- [ ] Register the resource providers once in the subscription: `Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.DBforPostgreSQL`, `Microsoft.Insights`, `Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.OperationalInsights` (`az provider register --namespace <name>`).
- [ ] Create the resource group the API lives in: `az group create --name <rg> --location <region>`. The deployment never creates one.
- [ ] Create the deployment identity in a separate resource group, so it can't change its own credentials: `az identity create --name passel-github-deploy --resource-group <ops-rg>`.
- [ ] Give it a federated credential for the environment: `az identity federated-credential create --name github-production --identity-name passel-github-deploy --resource-group <ops-rg> --issuer https://token.actions.githubusercontent.com --subject repo:Invex-Games/passel:environment:production --audiences api://AzureADTokenExchange`.
- [ ] Assign it roles on `<rg>` only: **Contributor**, and **Role Based Access Control Administrator** limited to the two roles Aspire assigns (AcrPull and Key Vault Secrets User); the condition is in [Deploying](../guides/deploying.md#role-assignments).
- [ ] In GitHub, create the `production` environment, with required reviewers and deployments limited to `main`.
- [ ] Add the variables (to the environment or the repository): `AZURE_CLIENT_ID` (the identity's client id), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `GOOGLE_WEB_CLIENT_ID`, and `CHROME_EXTENSION_CLIENT_ID` if it isn't set already.
- [ ] Generate the Sqids alphabet once and add it as the environment secret `ENCODING_ALPHABET`. It must never change once ids are in use.
- [ ] Run the Deploy workflow, then check `/health` on the URL in the run's summary.
- [ ] When the domain is ready: add its DNS records, set `API_DOMAIN`, deploy, create the managed certificate, set `API_CERTIFICATE_NAME` and deploy again ([Custom domain](../guides/deploying.md#custom-domain)).
