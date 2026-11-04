# Releasing and Deploying

The API runs on Azure Container Apps with an Azure Database for PostgreSQL flexible server; [ADR 0003](../decisions/0003-hosting-on-azure.md) records the choices and their cost. Everything Azure is modelled in the AppHost (`src/webapi/Invex.Passel.AppHost/AzureHosting.cs`) and deployed with `aspire deploy`, from a manual workflow. Nothing deploys on a merge.

The extension and the mobile app are not deployed from here yet; their release builds are described in [Build Targets](../reference/build-targets.md#release-builds).

## Versions

Versions come from GitVersion and the conventional commit prefixes described in `CONTRIBUTING.md`. Every push to `main` runs the `Build` workflow, which tests everything and attaches the release builds to the run. The Deploy workflow deploys the commit it was started on, and tags the API's image with that commit's version, so the image in the registry, the revision in Azure and the commit always match.

## What gets deployed

| Resource | Name in the AppHost | Purpose |
|---|---|---|
| Container Apps environment, with a container registry | `passel-env` | Runs the API and the migration job; the registry holds their images |
| Container app | `invex-passel-webapi` | The API: one replica, 0.5 vCPU, 1 GiB, HTTPS ingress, readiness on `/health` and liveness on `/alive` |
| Container Apps job | `passel-migrate` | The API's image started as `db-migrate`: applies the schema and grants the API's role its privileges |
| PostgreSQL flexible server and database | `passel-pg`, `passel-db` | PostgreSQL 18, Burstable B1ms, 32 GB with auto-grow, 14 days of point-in-time restore, Entra authentication only |
| Key Vault | `passel-kv` | The Sqids alphabet |
| Log Analytics workspace and Application Insights | `passel-logs`, `passel-insights` | Container logs, and the API's logs, traces and metrics |
| Managed identities | `passel-api-identity` and one per job and environment | What each resource signs in as; no passwords |

Azure names are derived from these and made unique per resource group, so the server, the vault and the environment get a suffix (`passelpg-…`, `passelkv-…`, `passelenv…`). The container app and the job keep their names.

## The Deploy workflow

`Deploy` runs on manual dispatch only (Actions, Deploy, Run workflow), in the `production` environment, so its protection rules apply before anything runs. One run deploys at a time; a second one waits rather than cancelling the first. The workflow signs in to Azure with `azure/login` and the deployment identity's federated credential, then runs `DeployWebApi`, which:

1. Checks that the Google client ids are real client ids and that the Sqids alphabet is set.
2. Writes the Bicep with `aspire publish` and attaches it to the run as `AzureInfrastructure`, a record of what was provisioned.
3. Runs `aspire deploy --environment Production`. Aspire provisions or updates every resource in the resource group, builds the two images with `dotnet publish` (the .NET SDK's container support) and pushes them with the version tag, updates the migration job and runs it, and only once the migration has succeeded updates the API's container app. Container Apps then starts the new revision and moves traffic to it when its readiness probe passes.

If the migration fails, the deployment stops before the API is touched, and the running revision keeps serving. If a new revision never becomes ready (it refuses to start on a schema it doesn't expect, for example), the previous one keeps serving too.

To see the Bicep without deploying, run `dotnet atom PublishAzureInfrastructure`; it writes the files to `atom-publish/AzureInfrastructure` without signing in to anything. Every pull request runs it too, so a change that breaks the Azure model fails in review.

## One-off setup

These are the steps of the checklist in [ADR 0003](../decisions/0003-hosting-on-azure.md#one-off-setup-by-the-owner), in order. They need someone who can create resource groups and role assignments in the subscription; the deployment identity itself only ever gets rights in the API's resource group.

### Azure

Register the resource providers the deployment uses, once per subscription:

```
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.ContainerRegistry
az provider register --namespace Microsoft.DBforPostgreSQL
az provider register --namespace Microsoft.Insights
az provider register --namespace Microsoft.KeyVault
az provider register --namespace Microsoft.ManagedIdentity
az provider register --namespace Microsoft.OperationalInsights
```

Create the API's resource group, and a separate one for the deployment identity. The identity lives apart so that its rights in the API's group don't include changing its own credentials:

```
az group create --name <rg> --location <region>
az group create --name <ops-rg> --location <region>
az identity create --name passel-github-deploy --resource-group <ops-rg>
```

Give the identity a federated credential that only a workflow job in the `production` environment of this repository can use:

```
az identity federated-credential create --name github-production --identity-name passel-github-deploy --resource-group <ops-rg> --issuer https://token.actions.githubusercontent.com --subject repo:Invex-Games/pushin:environment:production --audiences api://AzureADTokenExchange
```

Note the identity's `clientId` and `principalId` (`az identity show --name passel-github-deploy --resource-group <ops-rg>`), and the tenant id (`az account show --query tenantId`).

### Role assignments

The deployment identity needs **Contributor** on the API's resource group to create and update the resources, and **Role Based Access Control Administrator** there for the two role assignments Aspire makes: AcrPull for the environment's identity on the registry, and Key Vault Secrets User for the API's identity on the vault. The condition limits it to those two roles:

```
az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal --role Contributor --scope /subscriptions/<sub>/resourceGroups/<rg>

az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal --role "Role Based Access Control Administrator" --scope /subscriptions/<sub>/resourceGroups/<rg> --condition-version 2.0 --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 4633458b-17de-408a-b874-0445c86b69e6})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 4633458b-17de-408a-b874-0445c86b69e6}))"
```

The deployment identity gets no access to the database: the migration runs inside Azure as the job's own identity.

### GitHub

Create the `production` environment (Settings, Environments), with required reviewers and deployment branches limited to `main`. Then add these variables to the environment, or to the repository if other workflows need them too:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | The deployment identity's client id |
| `AZURE_TENANT_ID` | The tenant id |
| `AZURE_SUBSCRIPTION_ID` | The subscription id |
| `AZURE_RESOURCE_GROUP` | `<rg>` |
| `AZURE_LOCATION` | `<region>`, for example `australiaeast` |
| `GOOGLE_WEB_CLIENT_ID` | The Google web client id, the API's token audience ([Google OAuth Clients](../getting-started/google-oauth.md)) |
| `CHROME_EXTENSION_CLIENT_ID` | The extension's client id, already used by the `Build` workflow |
| `API_DOMAIN`, `API_CERTIFICATE_NAME` | Empty until the [custom domain](#custom-domain) is set up |

And one environment secret, `ENCODING_ALPHABET`: the Sqids alphabet for public ids, a shuffle of the 62 letters and digits. Generate it once and keep it: changing it later makes every id a client holds invalid.

```
pwsh -c "-join ([char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789' | Get-Random -Shuffle)"
```

### First deployment

Run the Deploy workflow from `main`. The first run creates everything and takes several minutes, most of it the database server. The run's log ends with the API's URL (`https://invex-passel-webapi.<environment domain>`); `/health` there should answer `Healthy`.

### Custom domain

The clients' production builds call `https://api.passel.link`, so the API needs that domain bound with a certificate. Container Apps validates the domain before it accepts it, and issues a managed certificate only once the domain is bound, so it takes two deployments:

1. Look up the app's host name and the environment's verification id:
   ```
   az containerapp show --name invex-passel-webapi --resource-group <rg> --query properties.configuration.ingress.fqdn --output tsv
   az containerapp env list --resource-group <rg> --query "[0].properties.customDomainConfiguration.customDomainVerificationId" --output tsv
   ```
2. Add a CNAME record `api` pointing at the host name, and a TXT record `asuid.api` holding the verification id.
3. Set `API_DOMAIN` to `api.passel.link` and deploy. The domain is added without a certificate, and the API's `AllowedHosts` now includes it.
4. Create the managed certificate, then set `API_CERTIFICATE_NAME` to its name and deploy again:
   ```
   az containerapp env certificate create --resource-group <rg> --name <environment name> --certificate-name api-passel-link --hostname api.passel.link --validation-method CNAME
   ```

Every later deployment keeps the binding, because both values come from the variables each time.

## The database

Three kinds of role exist on the server:

| Role | Who | Can |
|---|---|---|
| Microsoft Entra administrator | The migration job's identity, made one by the deployment | Change the schema; create roles. Owns the tables |
| `passel_app` | Nobody logs in as it | Select, insert, update and delete rows, use the sequences and run the functions in the store's schema |
| The API's role, named after `passel-api-identity` | The API, with an Entra token | What `passel_app` can, nothing more |

`db-migrate` ([Web API](../components/web-api.md#schema-in-production)) creates the last two the first time and is a no-op for them afterwards. The API checks at startup that the schema matches what it expects and refuses to start otherwise, so a revision deployed without its migration never takes traffic.

A schema change reaches production at the next deployment, applied by the migration job while the previous revision is still serving. So a change must work with the code before it as well as after it: add a column or a table in one release, and stop using or drop the old one in a later one. Marten's `CreateOrUpdate` adds and alters, but never drops a table.

To see what a migration would change without applying it, run `db-patch` against the database: it writes the statements to a file instead of running them. Locally it uses the connection string from `ConnectionStrings__passel-db` or the API's `appsettings.Development.json` ([Configuration](../reference/configuration.md#web-api)):

```
dotnet run --project src/webapi/Invex.Passel.WebApi -- db-patch patch.sql
```

To look at the database yourself, add your own account as a Microsoft Entra administrator of the server (portal, the server, Security, Authentication), then connect with a token as the password:

```
az account get-access-token --resource-type oss-rdbms --query accessToken --output tsv
psql "host=<server>.postgres.database.azure.com dbname=passel-db user=<you@example.com> sslmode=require"
```

### Backups and restore

The server keeps automatic backups for 14 days, and can be restored to any point in that window. A restore creates a new server rather than rewinding the existing one:

```
az postgres flexible-server restore --resource-group <rg> --name <new-server> --source-server <server> --restore-time 2026-11-04T09:00:00Z
```

The deployment keeps pointing at the original server, so copy what you need back from the restored one (`pg_dump` and `pg_restore`), then delete it.

## Rolling back

Run the Deploy workflow on the commit to go back to (the branch or tag picker of the run dialog). Its image is rebuilt with its own version tag, and its migration job brings the schema back to what that release expects: an index or a function the newer release added is dropped, a table it added stays and is ignored. A column is never dropped, and the API's startup check counts one it doesn't know as a mismatch, so rolling back across a release that added columns to a table means dropping them by hand first; `db-patch` (below) writes the statements.

## Logs and telemetry

The API's requests, dependencies, exceptions and logs are in Application Insights (`passel-insights`). Container output, including the migration job's, is in the Log Analytics workspace (`passel-logs`), table `ContainerAppConsoleLogs_CL`; each job run's status is also on the job's Execution history page in the portal. Both keep data for the workspace's default of 30 days.

None of it holds what users send: requests are recorded by path (packet ids, never content) with the query redacted, SQL without parameter values, and logs name packet ids. The page fetches for link previews go to the hosts users link, so they are left out of the dependencies, the outgoing HTTP and DNS metrics carry no host name, and the reason a link got no preview is logged at Debug, which Production doesn't keep. A new outgoing request to a URL a user supplied must call `ExcludeFromTelemetry` on its `HttpRequestMessage` in the same way.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| The workflow fails at Azure login | The federated credential's subject doesn't match: the job must run in the `production` environment of this repository |
| `AuthorizationFailed` while provisioning | A missing role on the resource group, or an unregistered resource provider |
| The deployment fails at `run-database-migration` | The migration job failed; its output is in the workspace. The API was not changed |
| The new revision never becomes ready | The API refused to start: usually a schema it doesn't expect, or a missing setting. The revision's console log says which |
| Every request gets `400 Invalid Hostname` | The host isn't in `AllowedHosts`: set `API_DOMAIN` for a custom domain |
| Every request gets `401` | `GOOGLE_WEB_CLIENT_ID` or `CHROME_EXTENSION_CLIENT_ID` doesn't match the client the token was issued to |

## What was verified locally

Nothing has been deployed yet: the setup above needs the owner's subscription. Without it, these were checked offline:

- `aspire publish` writes the Bicep, `aspire do diagnostics` shows the migration ordered between the job and the API, and the run-mode model of the AppHost is the same as before.
- The image built with `dotnet publish -t:PublishContainer` runs `db-migrate` against PostgreSQL 18 and then serves `/health` as a role that only has DML, refusing other hosts with 400.
- The API in Production refuses to start on a database that hasn't been migrated, and starts with an Application Insights connection string set.
- The integration tests run the migration twice and the API on the DML-only role, and check that the role can't create or alter tables.

What can only be checked in Azure: the first `aspire deploy`, the Entra mapping of the API's role (a security label set by `db-migrate`), the probes' `Host` header, and the custom-domain steps.
