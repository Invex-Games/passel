# Testing

Each component has its own test suite. None of them needs Docker, a database or a running API.

| Suite | Tooling | Run directly | Run with Atom |
|---|---|---|---|
| Web API | xUnit v3 on Microsoft.Testing.Platform | `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/Invex.Passel.WebApi.Tests.csproj` | `dotnet atom TestWebApi` |
| Chrome extension | TypeScript type-check, then Vitest | `npm run typecheck` and `npm test` in `src/chrome-extension` | `dotnet atom TestChromeExtension` |
| Mobile app | `flutter analyze`, then `flutter test` | Both, in `src/mobile_app` | `dotnet atom TestMobileApp` |

`dotnet atom Test` runs all three.

## Notes

- `global.json` selects Microsoft.Testing.Platform. Pass the test project with `--project`; a bare `dotnet test <path>` is parsed differently in this mode.
- `dotnet atom TestMobileApp` uses whichever Flutter is on your `PATH`. Unlike `BuildMobileAppAndroid`, it never switches your SDK to the `flutter-version` the build pins.
- The Web API tests are pure unit tests of endpoint helpers, the event hub, notification payloads and id encoding. Changes to Marten mappings or SQL are not covered, so check those by running the AppHost.
