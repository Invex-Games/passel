# Build Targets

The build is an [Atom](https://github.com/Invex-Games/atom) project in `_atom/`. Run `dotnet tool restore` once, then run a target from the repository root with `dotnet atom <Target>`. To list the targets and their parameters, run `dotnet run --project _atom -- --help` (the help of the tool itself describes the tool, not the targets). With the tool installed globally, `atom <Target>` also works from PowerShell and cmd.

Parameters can be passed on the command line (`--mobile-app-flavor production`), or set in `_atom/appsettings.json`. Secrets are read from Azure Key Vault (`invex-passel`).

## Development

| Target | Description |
|---|---|
| `SetupDevEnvironment` | Checks the tools you need, Docker, and the HTTPS development certificate. Creates `src/mobile_app/.env`, then restores npm and Flutter packages. |
| `Test` | Runs `TestWebApi`, `TestChromeExtension` and `TestMobileApp` |
| `TestWebApi` | `dotnet test` on the Web API unit tests, then on the integration tests, which start PostgreSQL and Azurite in Docker containers |
| `TestChromeExtension` | Type-checks every member of the npm workspace (the shared packages, the extension and the web app) and runs their Vitest suites with coverage; keeps the lcov reports in `atom-publish/ChromeExtensionCoverage` |
| `TestMobileApp` | `flutter analyze` and `flutter test --coverage`; keeps the lcov report in `atom-publish/MobileAppCoverage` |
| `BuildDocs` | Builds this site into `_site/` and `atom-publish/GeneratedDocs` |
| `ServeDocs` | Builds the docs and serves them at `http://localhost:8080` |
| `Gen` | Regenerates the GitHub workflows and Dependabot configuration under `.github/` from `_atom/IBuild.cs`; see [Continuous Integration](../guides/ci.md) |

## Release builds

| Target | Output | Parameters |
|---|---|---|
| `BuildWebApi` | Published API, in `atom-publish/Invex.Passel.WebApi` | None |
| `BuildChromeExtension` | `atom-publish/ChromeExtension/chrome-extension.zip` | `api-base-url-production` (the extension calls `<url>/api/packets`), `chrome-extension-client-id` |
| `BuildWebApp` | The web app's static site (landing, privacy and terms pages, and the app), in `atom-publish/WebApp`. The `Build` workflow runs it from the `API_BASE_URL` and `GOOGLE_WEB_CLIENT_ID` repository variables; see [Hosting](../components/web-app.md#hosting) | `api-base-url-production` (an `https` origin with no path; the app calls `<url>/api/packets`), `server-client-id` (the web client id; a headless run fails without a real one, an interactive one falls back to `src/web/.env`) |
| `BuildMobileAppAndroid` | A debug APK (development) or a signed, obfuscated app bundle plus symbols (production), in `atom-publish/android` | `mobile-app-flavor`, `api-base-url-production` / `api-base-url-development`, `server-client-id`, `mobile-app-client-id`, plus the `android-keystore-*` secrets for production |
| `PublishImages` | The API image (`passel-webapi`, from the .NET SDK's container support) and the web image (`passel-web`, Caddy with the site `BuildWebApp` built), tagged with the version and `sha-<commit>`. Pushed to the registry only from `refs/heads/main` with `push-images`; otherwise loaded into the local Docker daemon as `invex-games/passel-webapi` and `invex-games/passel-web`. Runs after `TestWebApi` | `container-registry` (`ghcr.io`), `container-namespace` (`invex-games`), `push-images` |
| `PublishDeployBundle` | The deploy bundle image (`passel-deploy`): the compose files and scripts in `deploy/compose`, `FROM scratch`, with the same tags and push rules as `PublishImages`. Runs after `TestWebApi` | The same as `PublishImages` |
| `PublishDocs` | The site built by `BuildDocs`, force-pushed to the `gh-pages` branch for GitHub Pages | `github-token` |

`BuildMobileAppAndroid` checks out the Flutter version in `flutter-version` (`_atom/appsettings.json`). If there's no Flutter on your `PATH`, it clones that version into `.flutter/`. Running it locally can therefore switch the version of your own Flutter SDK. The client-id parameters only take effect when they're real Google client ids (ending in `.apps.googleusercontent.com`). Otherwise the build logs a warning and uses the values in `src/mobile_app/.env`.

## Deployment

The versions come from GitVersion (`GitVersion.yml`), which `IBuild` uses as its version and build id provider: `SetupBuildInfo` prints the version, and the image targets read the one it stored, so run it first locally. An image tag is the version with `+` and `/` replaced by `-`. A version tag already in the registry is never moved: the image targets skip a push the registry already has for the same commit, and fail when the tag names another.

| Target | What it does | Parameters |
|---|---|---|
| `ValidateCompose` | `docker compose config` over `deploy/compose/compose.yaml`, alone and with each override, with every profile on and the placeholders in `ci.env`, then shellcheck over the deploy scripts (in the `koalaman/shellcheck` image when shellcheck isn't installed). The `Validate` workflow runs it on every pull request. | None |
| `DeployCompose` | Deploys a tag to the self-hosted server: checks that the three images exist, then runs `ssh` to the deploy gate with `deploy <tag>` and the registry token on stdin. Needs the `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS` and `GHCR_PULL_TOKEN` environment variables; the `DeployCompose` workflow sets them. See [Releasing and Deploying](../guides/deploying.md#the-deploycompose-workflow) | `image-tag` (blank for this build's version), `migrate` (default `true`; `false` for a rollback), `deploy-host`, `deploy-user` |
| `PublishAzureInfrastructure` | Writes the Bicep of the Azure deployment from the AppHost into `atom-publish/AzureInfrastructure`, without signing in to Azure. The `Validate` workflow runs it on every pull request. | None |
| `DeployWebApi` | Runs `aspire deploy`: provisions or updates the Azure resources, builds the images with the .NET SDK's container support, runs the database migration job, then updates the API. Also attaches the Bicep as `AzureInfrastructure`. Needs a signed-in Azure CLI; the `Deploy` workflow signs in with OIDC. | `azure-subscription-id`, `azure-resource-group`, `azure-location`, `server-client-id` (the Google web client id), `chrome-extension-client-id`, optionally `api-domain` and `api-certificate-name`, and the `Parameters__encoding-alphabet` environment variable |

`PublishAzureInfrastructure` and `DeployWebApi` belong to the legacy Azure deployment and run the Aspire CLI pinned in `dotnet-tools.json` (`dotnet aspire`). See [Releasing and Deploying](../guides/deploying.md) for the one-off Azure setup `DeployWebApi` needs, and [Configuration](configuration.md#azure-deployment) for where each value ends up.
