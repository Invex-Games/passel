# Build Targets

The build is an [Atom](https://github.com/Invex-Games/atom) project in `_atom/`. Run `dotnet tool restore` once, then run a target from the repository root with `dotnet atom <Target>`. To list the targets and their parameters, run `dotnet run --project _atom -- --help` (the help of the tool itself describes the tool, not the targets). With the tool installed globally, `atom <Target>` also works from PowerShell and cmd.

Parameters can be passed on the command line (`--mobile-app-flavor production`), or set in `_atom/appsettings.json`. Secrets are read from Azure Key Vault (`invex-passel`).

## Development

| Target | Description |
|---|---|
| `SetupDevEnvironment` | Checks the tools you need, Docker, and the HTTPS development certificate. Creates `src/mobile_app/.env`, then restores npm and Flutter packages. |
| `Test` | Runs `TestWebApi`, `TestChromeExtension` and `TestMobileApp` |
| `TestWebApi` | `dotnet test` on the Web API unit tests, then on the integration tests, which start PostgreSQL in a Docker container |
| `TestChromeExtension` | Type-checks the extension and runs Vitest |
| `TestMobileApp` | `flutter analyze` and `flutter test --coverage`; keeps the lcov report in `atom-publish/MobileAppCoverage` |
| `BuildDocs` | Builds this site into `_site/` and `atom-publish/GeneratedDocs` |
| `ServeDocs` | Builds the docs and serves them at `http://localhost:8080` |
| `Gen` | Regenerates the GitHub workflows and Dependabot configuration under `.github/` from `_atom/IBuild.cs`; see [Continuous Integration](../guides/ci.md) |

## Release builds

| Target | Output | Parameters |
|---|---|---|
| `BuildWebApi` | Published API, in `atom-publish/Invex.Passel.WebApi` | None |
| `BuildChromeExtension` | `atom-publish/ChromeExtension/chrome-extension.zip` | `api-base-url-production` (the extension calls `<url>/api/packets`), `chrome-extension-client-id` |
| `BuildMobileAppAndroid` | A debug APK (development) or a signed, obfuscated app bundle plus symbols (production), in `atom-publish/android` | `mobile-app-flavor`, `api-base-url-production` / `api-base-url-development`, `server-client-id`, `mobile-app-client-id`, plus the `android-keystore-*` secrets for production |
| `PublishDocs` | The site built by `BuildDocs`, force-pushed to the `gh-pages` branch for GitHub Pages | `github-token` |

`BuildMobileAppAndroid` checks out the Flutter version in `flutter-version` (`_atom/appsettings.json`). If there's no Flutter on your `PATH`, it clones that version into `.flutter/`. Running it locally can therefore switch the version of your own Flutter SDK. The client-id parameters only take effect when they're real Google client ids (ending in `.apps.googleusercontent.com`). Otherwise the build logs a warning and uses the values in `src/mobile_app/.env`.

## Deployment

| Target | What it does | Parameters |
|---|---|---|
| `PublishAzureInfrastructure` | Writes the Bicep of the Azure deployment from the AppHost into `atom-publish/AzureInfrastructure`, without signing in to Azure. The `Validate` workflow runs it on every pull request. | None |
| `DeployWebApi` | Runs `aspire deploy`: provisions or updates the Azure resources, builds the images with the .NET SDK's container support, runs the database migration job, then updates the API. Also attaches the Bicep as `AzureInfrastructure`. Needs a signed-in Azure CLI; the `Deploy` workflow signs in with OIDC. | `azure-subscription-id`, `azure-resource-group`, `azure-location`, `server-client-id` (the Google web client id), `chrome-extension-client-id`, optionally `api-domain` and `api-certificate-name`, and the `Parameters__encoding-alphabet` environment variable |

Both run the Aspire CLI pinned in `dotnet-tools.json` (`dotnet aspire`). See [Releasing and Deploying](../guides/deploying.md) for the one-off Azure setup `DeployWebApi` needs, and [Configuration](configuration.md#azure-deployment) for where each value ends up.
