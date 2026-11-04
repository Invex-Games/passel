# Testing

Each component has its own test suite. Only the Web API integration tests need Docker; nothing needs a running API.

| Suite | Tooling | Run directly | Run with Atom |
|---|---|---|---|
| Web API | xUnit v3 on Microsoft.Testing.Platform | `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/Invex.Passel.WebApi.Tests.csproj` | `dotnet atom TestWebApi` |
| Web API integration | xUnit v3, with PostgreSQL in a Docker container (Testcontainers) | `dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj` | `dotnet atom TestWebApi`, after the unit tests |
| Shared TypeScript packages and the Chrome extension | TypeScript type-check of every workspace member, then Vitest: `src/packages/core/test` for the platform-free logic, `src/chrome-extension/test` for the extension's own modules, against a stubbed `chrome` | `npm run typecheck` and `npm test` in `src` | `dotnet atom TestChromeExtension`, with coverage |
| Mobile app | `flutter analyze`, then `flutter test` | Both, in `src/mobile_app` | `dotnet atom TestMobileApp`, with coverage |

`dotnet atom Test` runs all three.

## Notes

- `global.json` selects Microsoft.Testing.Platform. Pass the test project with `--project`; a bare `dotnet test <path>` is parsed differently in this mode.
- `dotnet atom TestMobileApp` uses whichever Flutter is on your `PATH`. Unlike `BuildMobileAppAndroid`, it never switches your SDK to the `flutter-version` the build pins.
- The extension's tests (`src/chrome-extension/test`) run its modules unbundled against `test/chrome.ts`, a stand-in for `chrome.*` with storage that keeps what is written, events a test can fire and `vi.fn()`s for the rest; `test/setup.ts` supplies the `__CONFIG__` the build would. The service worker's modules run in Node, which like a service worker has `fetch` and streams but no DOM: sign-in (the authorization URL for each prompt mode, the state, nonce, audience and expiry checks, account changes, development users), the API client (the 401 refresh, retry and sign-out matrix, ProblemDetails messages, the endpoints, the event stream and its idle timeout, with `fetch` stubbed), the live connection's port lifecycle and backoff, and the worker's message routing, delete scheduling and undo, context menus, keyboard commands and the `passel` keyword, with fake timers. The popup's composer and controller run in jsdom on the real `static/popup.html`, as the popup and as the side panel. `npm run test:coverage` in `src` writes each package's line and branch coverage to its `coverage/lcov.info` (with an HTML report beside it), and `dotnet atom TestChromeExtension` keeps both as the `ChromeExtensionCoverage` artifact (`atom-publish/ChromeExtensionCoverage/chrome-extension.lcov.info` and `core.lcov.info`).
- The mobile app's widget tests (`test/packets/*_test.dart`, `test/auth/login_page_test.dart`) drive the screens with the fakes in `test/fakes.dart`: the feed page signed in and out with its sync label, the list's loading, failure and empty states and its loading of older items, the send sheet, the tiles (copy, share and links, with the plugin channels mocked) and delete with undo. `dotnet atom TestMobileApp` runs `flutter test --coverage` and keeps the line coverage of `lib/`, `src/mobile_app/coverage/lcov.info`, as the `MobileAppCoverage` artifact (`atom-publish/MobileAppCoverage/lcov.info` locally, attached to the workflow run in CI).
- The Web API unit tests cover endpoint helpers, the event hub, notification payloads, id encoding, and the link preview guards and extractor: the table of refused and allowed addresses, the URL checks, redirects, and the size and time limits, against a fake handler and a stand-in for DNS. The Marten mappings, the SQL, the live-update path, the preview fetch end to end and the contract files are covered by the integration tests, against a real PostgreSQL; see [Integration tests](../components/web-api.md#integration-tests).

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
