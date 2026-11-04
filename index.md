---
_layout: landing
---

# Passel

Passel sends links and text between your devices. Push something from your browser and it shows up on your phone, live.

The repository holds the apps that share one API:

| Component | Stack | Source |
|---|---|---|
| [Web API](docs/components/web-api.md) | ASP.NET Core (.NET 10), Marten on PostgreSQL, hosted locally by .NET Aspire | `src/webapi` |
| [Chrome Extension](docs/components/chrome-extension.md) | TypeScript, Manifest V3, esbuild | `src/chrome-extension` |
| [Web App](docs/components/web-app.md) | TypeScript, Preact, esbuild; also the landing, privacy and terms pages | `src/web` |
| [Mobile App](docs/components/mobile-app.md) | Flutter (Android), Riverpod | `src/mobile_app` |

Builds, tests and docs are driven by [Atom](https://github.com/Invex-Games/atom); see [Build Targets](docs/reference/build-targets.md).

## Start here

1. [Install the prerequisites](docs/getting-started/prerequisites.md), then run `dotnet tool restore` and `dotnet atom SetupDevEnvironment` to check them.
2. [Set up the Google OAuth clients](docs/getting-started/google-oauth.md) the apps sign in with.
3. [Run everything locally](docs/getting-started/running-locally.md) with the Aspire AppHost.
