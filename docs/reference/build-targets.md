# Build Targets

The build is an [Atom](https://github.com/Invex-Games/atom) project in `_atom/`. Run a target from the repository root with `atom <Target>`, and list targets and their parameters with `atom -h`.

Parameters can be passed on the command line (`--mobile-app-flavor production`), or set in `_atom/appsettings.json`. Secrets are read from Azure Key Vault (`invex-passel`).

## Development

| Target | Description |
|---|---|
| `SetupDevEnvironment` | Checks the tools you need, Docker, and the HTTPS development certificate. Creates `src/mobile_app/.env`, then restores npm and Flutter packages. |
| `Test` | Runs `TestWebApi`, `TestChromeExtension` and `TestMobileApp` |
| `TestWebApi` | `dotnet test` on the Web API test project |
| `TestChromeExtension` | Type-checks the extension and runs Vitest |
| `TestMobileApp` | `flutter analyze` and `flutter test` |
| `BuildDocs` | Builds this site into `_site/` and `atom-publish/GeneratedDocs` |
| `ServeDocs` | Builds the docs and serves them at `http://localhost:8080` |

## Release builds

| Target | Output | Parameters |
|---|---|---|
| `BuildWebApi` | Published API, in `atom-publish/Invex.Passel.WebApi` | None |
| `BuildChromeExtension` | `atom-publish/ChromeExtension/chrome-extension.zip` | `api-base-url-production` (the extension calls `<url>/api/packets`), `chrome-extension-client-id` |
| `BuildMobileAppAndroid` | A debug APK (development) or a signed, obfuscated app bundle plus symbols (production), in `atom-publish/android` | `mobile-app-flavor`, `api-base-url-production` / `api-base-url-development`, `server-client-id`, `mobile-app-client-id`, plus the `android-keystore-*` secrets for production |

`BuildMobileAppAndroid` checks out the Flutter version in `flutter-version` (`_atom/appsettings.json`). If there's no Flutter on your `PATH`, it clones that version into `.flutter/`. Running it locally can therefore switch the version of your own Flutter SDK. The client-id parameters only take effect when they're real Google client ids (ending in `.apps.googleusercontent.com`). Otherwise the build logs a warning and uses the values in `src/mobile_app/.env`.
