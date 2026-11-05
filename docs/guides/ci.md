# Continuous Integration

The workflows under `.github/workflows/` and the Dependabot configuration in `.github/dependabot.yml` are generated from `_atom/IBuild.cs` by the [Atom GitHub workflows module](https://github.com/Invex-Games/atom). Don't edit the YAML by hand: change the definitions and run `dotnet atom Gen`. Running any target locally in interactive mode regenerates the files too, and a headless run (CI) fails early if the committed files are out of date.

On Windows the generator writes CRLF line endings, so after an interactive run git lists the files under `.github/` as modified although only the line endings differ. `git checkout -- .github` puts them back, and a headless run never rewrites them.

## Workflows

| Workflow | Runs on | Jobs |
|---|---|---|
| `Validate` | Pull requests into `main`, manual dispatch | `TestWebApi`, `TestChromeExtension` and `TestMobileApp` (their coverage reports attached to the run as `ChromeExtensionCoverage` and `MobileAppCoverage`), `PublishAzureInfrastructure` (the Azure deployment's Bicep attached as `AzureInfrastructure`, nothing signed in to), `ValidateCompose` (the compose files and deploy scripts checked, nothing run) and `BuildDocs`, in parallel; nothing is published |
| `Build` | Pushes to `main`, manual dispatch | `SetupBuildInfo` (the GitVersion version), the three test suites (the extension's and the mobile app's coverage reports attached to the run), `BuildWebApi`, `BuildChromeExtension` and `BuildWebApp` (artifacts attached to the run), `PublishImages` and `PublishDeployBundle` (the API, web and deploy bundle images, pushed to GHCR from `main` only once `TestWebApi` has passed), `BuildDocs` and `PublishDocs` (the site force-pushed to the `gh-pages` branch) |
| `DeployCompose` | Manual dispatch only, with the inputs `target` (`preprod` or `production`, required), `image-tag` (blank for the run's own commit) and `migrate` | `SetupBuildInfo`, then `DeployCompose` in the GitHub environment named by `target`: deploys a tag to that self-hosted stack over SSH, or rolls back to an older one; see [Releasing and Deploying](deploying.md#the-deploycompose-workflow). One run at a time per stack (`deploy-compose-preprod`, `deploy-compose-production`); a second to the same stack waits instead of cancelling the first |
| `Deploy` | Manual dispatch only | `SetupBuildInfo`, then `DeployWebApi` in the `production` environment: signs in to Azure with OIDC and deploys the API; see [Releasing and Deploying](deploying.md). One run at a time; a second waits instead of cancelling the first |
| Dependabot | Weekly, Monday 06:00 Brisbane time | One grouped pull request each for NuGet, npm, pub and GitHub Actions, with `build(...)` commit prefixes so versions don't bump |

A newer run of `Validate` or `Build` on the same branch cancels the one still in progress; `DeployCompose` and `Deploy` runs queue instead. Every job checks the repository out and sets up the .NET 10 SDK; Node and Docker come with the runner (`TestWebApi` starts PostgreSQL and Azurite in containers for the integration tests), and `TestMobileApp` installs the Flutter version pinned in `_atom/appsettings.json` into `.flutter/` because runners don't have one.

The mobile app's release build (`BuildMobileAppAndroid`) is not in a workflow yet: it needs the Android keystore and client ids from Azure Key Vault, so the repository first needs an Azure identity with access to the vault.

## What the repository needs

| Setting | Where | Used by |
|---|---|---|
| `CHROME_EXTENSION_CLIENT_ID` | Settings, Secrets and variables, Actions, Variables | `BuildChromeExtension`, which otherwise has no Google client id outside a developer's `.env`, and `DeployWebApi`, which gives it to the API as an accepted audience |
| `GITHUB_TOKEN` | Provided by GitHub Actions | `PublishDocs`, which needs `contents: write` on the job; `PublishImages` and `PublishDeployBundle`, which need `packages: write` to push to GHCR; `DeployCompose`, which needs `packages: read` and hands the token to the server for the one deploy. All are declared on the jobs |
| `AZURE_VAULT_ADDRESS`, `AZURE_VAULT_TENANT_ID`, `AZURE_VAULT_APP_ID` (variables) and `AZURE_VAULT_APP_SECRET` (secret) | Same pages | Only targets that read Key Vault secrets; the current workflows don't, so these can stay unset until the mobile release build is added |
| `production` and `preprod` environments, each with its own `DEPLOY_HOST`, `DEPLOY_USER` and `DEPLOY_KNOWN_HOSTS` (variables) and `DEPLOY_SSH_KEY` (secret) | Settings, Environments | `DeployCompose`, in the environment its `target` names; see [Server setup](deploying.md#server-setup) and [Pre-prod and production on one server](deploying.md#pre-prod-and-production-on-one-server) |
| GHCR packages `passel-webapi`, `passel-web` and `passel-deploy` | Each package's settings, once the first build of `main` has pushed it | Private. Under Manage Actions access, this repository keeps the Admin role the first push gave it (or at least Write), or the package inherits access from the repository: `PublishImages` and `PublishDeployBundle` push to the packages, so a Read role breaks them, while `DeployCompose` only reads |
| `production` environment, with `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `GOOGLE_WEB_CLIENT_ID`, `API_DOMAIN` and `API_CERTIFICATE_NAME` (variables) and `ENCODING_ALPHABET` (secret) | Settings, Environments | `DeployWebApi` (legacy). The Azure side (the identity and its federated credential) is set up once as [Releasing and Deploying](deploying.md#one-off-setup) describes; until then the workflow has nothing to deploy to |
| GitHub Pages source | Settings, Pages: deploy from the `gh-pages` branch | Serving the published docs site |
| Branch protection on `main` | Settings, Branches | Require the `Validate` jobs to pass and at least one review before merging |

The `Build` workflow no longer reads the repository variables `GOOGLE_WEB_CLIENT_ID` and `API_BASE_URL`: the web app is built without them, and the web image takes the API's origin and the client id from the server's `.env` when it starts ([The web image](../components/web-app.md#the-web-image)). Once nothing else of yours uses them they can be deleted. The `production` environment's own `GOOGLE_WEB_CLIENT_ID` is a different variable, which the legacy `DeployWebApi` still reads.

## Running a workflow's target locally

Each job runs one target in headless mode, for example:

```
dotnet atom TestWebApi --headless
```

`--headless` makes the build fail instead of prompting for anything, which is also a quick way to find a target that would hang in CI.
