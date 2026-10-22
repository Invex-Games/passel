# Testing

Each component has its own test suite. Only the Web API integration tests need Docker; nothing needs a running API.

| Suite | Tooling | Run directly | Run with Atom |
|---|---|---|---|
| Web API | xUnit v3 on Microsoft.Testing.Platform | `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/Invex.Passel.WebApi.Tests.csproj` | `dotnet atom TestWebApi` |
| Web API integration | xUnit v3, with PostgreSQL in a Docker container (Testcontainers) | `dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj` | `dotnet atom TestWebApi`, after the unit tests |
| Chrome extension | TypeScript type-check, then Vitest | `npm run typecheck` and `npm test` in `src/chrome-extension` | `dotnet atom TestChromeExtension` |
| Mobile app | `flutter analyze`, then `flutter test` | Both, in `src/mobile_app` | `dotnet atom TestMobileApp`, with coverage |

`dotnet atom Test` runs all three.

## Notes

- `global.json` selects Microsoft.Testing.Platform. Pass the test project with `--project`; a bare `dotnet test <path>` is parsed differently in this mode.
- `dotnet atom TestMobileApp` uses whichever Flutter is on your `PATH`. Unlike `BuildMobileAppAndroid`, it never switches your SDK to the `flutter-version` the build pins.
- The mobile app's widget tests (`test/packets/*_test.dart`, `test/auth/login_page_test.dart`) drive the screens with the fakes in `test/fakes.dart`: the feed page signed in and out with its sync label, the list's loading, failure and empty states and its loading of older items, the send sheet, the tiles (copy, share and links, with the plugin channels mocked) and delete with undo. `dotnet atom TestMobileApp` runs `flutter test --coverage` and keeps the line coverage of `lib/`, `src/mobile_app/coverage/lcov.info`, as the `MobileAppCoverage` artifact (`atom-publish/MobileAppCoverage/lcov.info` locally, attached to the workflow run in CI).
- The Web API unit tests cover endpoint helpers, the event hub, notification payloads and id encoding. The Marten mappings, the SQL and the live-update path are covered by the integration tests, against a real PostgreSQL; see [Integration tests](../components/web-api.md#integration-tests).
