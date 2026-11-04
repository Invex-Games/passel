# ADR 0003: Hosting the API on Azure

**Status:** proposed, November 2026. The owner still has to confirm the subscription, the region and the services below, then do the one-off setup at the end; until then the Deploy workflow has nothing to run against. **Decides:** roadmap package 0.A2 (deploying the API), within the Azure choice of [ADR 0002](0002-product-decisions.md).

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

**Telemetry: Application Insights**, workspace-based on the same Log Analytics workspace. `ServiceDefaults` sends logs, traces and metrics with the Azure Monitor OpenTelemetry distro whenever `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, and to the OTLP endpoint otherwise, as before. Npgsql's per-command logs are kept to warnings in Production (`appsettings.Production.json`), so the workspace doesn't receive every SQL statement.

**Identities.** Every resource signs in as a user-assigned managed identity. The API has its own (`passel-api-identity`), which can read the Key Vault secret and is, for now, a Microsoft Entra administrator of the database server, because the API still applies the schema at startup; roadmap package 0.A3 moves that to the deployment. The environment's identity pulls images from the registry.

**Infrastructure as code: generated by Aspire.** The Azure resources are modelled in the AppHost (`AzureHosting.cs`) with the Aspire Azure hosting integrations, and only when it is published or deployed. Locally, `AddAzurePostgresFlexibleServer(...).RunAsContainer(...)` gives the same `passel-pg` container as before, and the Azure-only resources are never added, so `aspire run` and the tests need nothing from Azure. No Bicep is written by hand or committed. `aspire publish` writes it from the model, every pull request runs that through `PublishAzureInfrastructure` and attaches the files to the run for review, and `aspire deploy` provisions it.

**Images: the .NET SDK's container support.** `aspire deploy` builds the API's image with `dotnet publish` (`PublishContainer`), tags it with GitVersion's version for the commit, pushes it to the environment's registry and points the container app at it.

**Pipeline: a manual Deploy workflow.** `DeployWebApi` runs only on `workflow_dispatch`, in the `production` GitHub environment, and signs in with `azure/login` through a federated credential (OIDC): no Azure secret is stored in GitHub. Deployments queue rather than cancel each other. Merging this changes nothing until the owner has done the setup below.

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
- The AppHost uses an experimental Aspire API, the image push options (`ASPIREPIPELINES003`). An Aspire upgrade may change it.
- Nothing here has been provisioned. The Bicep and the image are verified locally (see [Deploying](../guides/deploying.md#what-was-verified-locally)); the first `aspire deploy` can only be checked in Azure.

## One-off setup by the owner

Commands use the Azure CLI. `<sub>`, `<rg>`, `<region>` and the names are placeholders, and the repository was `Invex-Games/pushin` at the time of writing. [Deploying](../guides/deploying.md) explains each step.

- [ ] Choose the subscription and region, confirm the services and cost above, decide on geo-redundant backup, and change this ADR's status to accepted.
- [ ] Register the resource providers once in the subscription: `Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.DBforPostgreSQL`, `Microsoft.Insights`, `Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.OperationalInsights` (`az provider register --namespace <name>`).
- [ ] Create the resource group the API lives in: `az group create --name <rg> --location <region>`. The deployment never creates one.
- [ ] Create the deployment identity in a separate resource group, so it can't change its own credentials: `az identity create --name passel-github-deploy --resource-group <ops-rg>`.
- [ ] Give it a federated credential for the environment: `az identity federated-credential create --name github-production --identity-name passel-github-deploy --resource-group <ops-rg> --issuer https://token.actions.githubusercontent.com --subject repo:Invex-Games/pushin:environment:production --audiences api://AzureADTokenExchange`.
- [ ] Assign it roles on `<rg>` only: **Contributor**, and **Role Based Access Control Administrator** limited to the two roles Aspire assigns (AcrPull and Key Vault Secrets User); the condition is in [Deploying](../guides/deploying.md#role-assignments).
- [ ] In GitHub, create the `production` environment, with required reviewers and deployments limited to `main`.
- [ ] Add the variables (to the environment or the repository): `AZURE_CLIENT_ID` (the identity's client id), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `GOOGLE_WEB_CLIENT_ID`, and `CHROME_EXTENSION_CLIENT_ID` if it isn't set already.
- [ ] Generate the Sqids alphabet once and add it as the environment secret `ENCODING_ALPHABET`. It must never change once ids are in use.
- [ ] Run the Deploy workflow, then check `/health` on the URL in the run's summary.
- [ ] When the domain is ready: add its DNS records, set `API_DOMAIN`, deploy, create the managed certificate, set `API_CERTIFICATE_NAME` and deploy again ([Custom domain](../guides/deploying.md#custom-domain)).
