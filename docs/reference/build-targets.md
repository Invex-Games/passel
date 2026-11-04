# Build Targets

The build is an [Atom](https://github.com/Invex-Games/atom) project in `_atom/`. Run `dotnet tool restore` once, then run a target from the repository root with `dotnet atom <Target>`. To list the targets and their parameters, run `dotnet run --project _atom -- --help` (the help of the tool itself describes the tool, not the targets). With the tool installed globally, `atom <Target>` also works from PowerShell and cmd.

Parameters can be passed on the command line (`--mobile-app-flavor production`), or set in `_atom/appsettings.json`. Secrets are read from Azure Key Vault (`invex-passel`).

## Development

| Target | Description |
|---|---|
| `SetupDevEnvironment` | Checks the tools you need, Docker, and the HTTPS development certificate. Creates `src/mobile_app/.env`, then restores npm and Flutter packages. |
| `Test` | Runs `TestWebApi`, `TestChromeExtension` and `TestMobileApp` |
| `TestWebApi` | `dotnet test` on the Web API unit tests, then on the integration tests, which start PostgreSQL in a Docker container |
| `TestChromeExtension` | Type-checks the npm workspace and runs Vitest with coverage; keeps the lcov reports in `atom-publish/ChromeExtensionCoverage` |
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
