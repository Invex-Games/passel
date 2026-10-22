# Testing

Each component has its own test suite. Only the Web API integration tests need Docker; nothing needs a running API.

| Suite | Tooling | Run directly | Run with Atom |
|---|---|---|---|
| Web API | xUnit v3 on Microsoft.Testing.Platform | `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/Invex.Passel.WebApi.Tests.csproj` | `dotnet atom TestWebApi` |
| Web API integration | xUnit v3, with PostgreSQL in a Docker container (Testcontainers) | `dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj` | `dotnet atom TestWebApi`, after the unit tests |
| Chrome extension | TypeScript type-check, then Vitest | `npm run typecheck` and `npm test` in `src/chrome-extension` | `dotnet atom TestChromeExtension` |
| Mobile app | `flutter analyze`, then `flutter test` | Both, in `src/mobile_app` | `dotnet atom TestMobileApp` |

`dotnet atom Test` runs all three.

## Notes

- `global.json` selects Microsoft.Testing.Platform. Pass the test project with `--project`; a bare `dotnet test <path>` is parsed differently in this mode.
- `dotnet atom TestMobileApp` uses whichever Flutter is on your `PATH`. Unlike `BuildMobileAppAndroid`, it never switches your SDK to the `flutter-version` the build pins.
- The Web API unit tests cover endpoint helpers, the event hub, notification payloads and id encoding. The Marten mappings, the SQL, the live-update path and the contract files are covered by the integration tests, against a real PostgreSQL; see [Integration tests](../components/web-api.md#integration-tests).

## Contracts

`contracts/` at the repository root is read by all three suites, so a change to the packet wire shape or to the feed rules fails a test on every side that has not followed it:

- The Web API integration tests generate `contracts/openapi.json` and `contracts/fixtures/` from the running API and fail when the committed files differ. To regenerate them, run the integration tests with `PASSEL_UPDATE_CONTRACTS=1` in the environment (Docker running):

  ```
  $env:PASSEL_UPDATE_CONTRACTS = "1"; dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj
  ```

  In a POSIX shell: `PASSEL_UPDATE_CONTRACTS=1 dotnet test --project …`. Review the diff and commit it with the change that caused it.
- The extension (`test/contracts.test.ts`) and the mobile app (`test/contracts_test.dart`) parse every fixture with their real parsers and run every scenario in `contracts/feed-scenarios.json` through their feed stores. A fixture file or a scenario action they do not know fails both suites until they handle it.
- `contracts/feed-scenarios.json` is written by hand and explains its own format. Add a scenario when a feed rule changes.

See [Contracts](../components/web-api.md#contracts) for what the files are and where the wire shape is declared.
