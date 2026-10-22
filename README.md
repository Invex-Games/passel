# Passel

https://passel.link

Send links and text between your devices. This repository contains:

- **Web API**: ASP.NET Core and Marten on PostgreSQL, hosted locally by .NET Aspire (`src/webapi`)
- **Chrome extension**: TypeScript, Manifest V3 (`src/chrome-extension`)
- **Mobile app**: Flutter for Android (`src/mobile_app`)

## Quick start

You need the .NET 10 SDK, Docker and Node.js. Flutter and the Dev Tunnels CLI are only needed for mobile work. The [Atom](https://github.com/Invex-Games/atom) build tool is pinned in `dotnet-tools.json`; `dotnet tool restore` installs it.

```
dotnet tool restore               # installs the Atom build tool
dotnet atom SetupDevEnvironment   # checks tools, prepares local config and packages
aspire run                        # or: dotnet run --project src/webapi/Invex.Passel.AppHost
```

The AppHost starts PostgreSQL, the API (`https://localhost:7030`), the extension's watch build and a dev tunnel for phones. On the first run, the Aspire dashboard asks for the Google OAuth client values.

## Docs

The developer docs live in `docs/` and build with DocFX:

```
dotnet atom ServeDocs             # http://localhost:8080
```

Start with [Prerequisites](docs/getting-started/prerequisites.md), [Google OAuth Clients](docs/getting-started/google-oauth.md) and [Running Locally](docs/getting-started/running-locally.md).

## Common commands

| Command | What it does |
|---|---|
| `dotnet atom Test` | Runs the Web API, extension and mobile test suites |
| `dotnet atom BuildWebApi` / `BuildChromeExtension` / `BuildMobileAppAndroid` | Release builds into `atom-publish/` |
| `dotnet run --project _atom -- --help` | Lists every target and its parameters |

## Contributing

Branch and commit conventions, the pull request checklist and what CI runs are in [CONTRIBUTING.md](CONTRIBUTING.md). Notable changes are listed in [CHANGELOG.md](CHANGELOG.md).
