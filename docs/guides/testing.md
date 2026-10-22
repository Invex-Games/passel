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
- The Web API unit tests cover endpoint helpers, the event hub, notification payloads and id encoding. The Marten mappings, the SQL and the live-update path are covered by the integration tests, against a real PostgreSQL; see [Integration tests](../components/web-api.md#integration-tests).
