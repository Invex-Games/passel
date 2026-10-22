# Prerequisites

## Tools

| Tool | Needed for | Required |
|---|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | The Web API, the Aspire AppHost and the Atom build | Yes |
| [Atom tool](https://github.com/Invex-Games/atom), installed by `dotnet tool restore` from `dotnet-tools.json` | Running build, test, setup and docs targets | Yes |
| [Docker](https://docs.docker.com/get-started/get-docker/) | The PostgreSQL container the AppHost starts | Yes |
| [Node.js](https://nodejs.org/) (with npm) | The Chrome extension | Yes |
| [Aspire CLI](https://aspire.dev/get-started/install-cli/) | `aspire run` (`dotnet run --project src/webapi/Invex.Passel.AppHost` works without it) | No |
| [Flutter](https://docs.flutter.dev/get-started/install) and the Android SDK | The mobile app; the SDK's `adb` connects a phone on USB to your local API | Mobile work only |
| [Dev Tunnels CLI](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started) | Reaching your local API from a phone that is not on USB | Only for that |

The API's HTTPS endpoint also needs a trusted ASP.NET Core development certificate:

```
dotnet dev-certs https --trust
```

## Check your machine

From the repository root, run:

```
dotnet tool restore
dotnet atom SetupDevEnvironment
```

The target:

- Reports any tools that are missing. It fails only if Docker or npm is missing.
- Warns you if Docker isn't running or the HTTPS development certificate isn't trusted.
- Creates `src/mobile_app/.env` from `.env.example` if it doesn't exist.
- Installs the Chrome extension's npm packages.
- Fetches the mobile app's Flutter packages.

It's safe to run again at any time. Next, [set up the Google OAuth clients](google-oauth.md).
