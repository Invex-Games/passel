# Roadmap

Everything from the evaluation and the [feature ideas](features.md), cut into work packages that can be picked up independently. Each package names the area it touches so that packages in different areas can run at the same time (in parallel sessions or by different people) without stepping on each other.

## How to read it

- **Lanes** are areas of the repository: **A** platform, operations and docs, **B** the API, **C** the extension, **D** the mobile app, **E** the web app, **F** product and design decisions.
- **Phases** are the suggested order. Inside a phase, packages in different lanes run in parallel; packages in the same lane that touch the same files are listed in the order they should land.
- **Size:** S under a day, M a few days, L about a week, XL several weeks.
- **Source** links the report that motivates the package: [Performance](performance.md), [Security](security.md), [UX](ux.md), [UI](ui-design.md), [Health](codebase-health.md), [DX](developer-experience.md), [Features](features.md), [Web app](web-app.md).

Decisions that gate packages are listed in the [overview](index.md); make them in the first week.

## Phase 0: safe to ship (one to two weeks)

Goal: the API deployed with CI in front of it, basic abuse protection, the storage model settled, and the fixes that would be embarrassing in front of users.

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 0.X | all | **Hygiene sweep, done first and alone:** `.gitattributes` with LF, `.editorconfig` to LF, strip BOMs, tool manifest for Atom and the README fix for target discovery, delete the stray keystore from the tree, untrack `.flutter-plugins`, commit `devtools_options.yaml`, trim the ignore file, vitest 5 and esbuild patch, align Aspire versions, Node-only npm scripts, `pubspec.yaml` cleanup, remove `avoid_print`, delete `CreateFilePacket` and the unused endpoint helpers, keep the personal device id out of `launch.json` | S to M | | Small edits across every area, so it lands before parallel work starts | DX 4, 5, 7, 11; Health 5, 11; Security 8, 9 |
| 0.A1 | A | CI through Atom's GitHub workflows module: `Test` on pull requests, `Build*` and docs publish on `main`, Dependabot for NuGet, npm, pub and actions, vulnerability scans; branch protection; CONTRIBUTING, LICENSE, CODEOWNERS, pull-request template, CHANGELOG from conventional commits; central package management and SDK, Node and Flutter pins | M | 0.X | `.github/`, `_atom/`, root files | DX 1, 2, 12; Health 7 |
| 0.A2 | A | Deploy the API: container image from `dotnet publish` with GitVersion tags, managed PostgreSQL, secrets from the host's store with the keys documented, health checks wired, OpenTelemetry to a real backend, backups, a `DeployWebApi` target and workflow | L | 0.A1 | `src/webapi`, `_atom/`, infrastructure | DX 1; Security 5 |
| 0.A3 | A | Schema and startup for production: `AutoCreate.None` with migrations applied by the pipeline, a DML-only database role, static code generation (`TypeLoadMode.Static`, `codegen write` in CI), `ApplyAllDatabaseChangesOnStartup` | S | 0.A2 | `Program.cs`, pipeline | Security 5; Performance 7 |
| 0.A4 | A | Dev tunnel: `WithExplicitStart()` now, `adb reverse` documented as the default, tunnel token from a dart-define when a remote device is needed, `RequireHost("localhost")` on the dev endpoints; remove the anonymous-dashboard variable | S | | `AppHost.cs`, `HostDevExtensions.cs`, `docs/` | Security 3, 7 |
| 0.B1 | B | Abuse controls and hygiene: rate limiter partitioned by user, per-user packet and byte quotas, caps on event streams per user and per instance, streams that end at token expiry, Kestrel connection limits, remove CORS until the web app needs it, HSTS, `AllowedHosts`, forwarded headers, fix the OIDC nonce setting and cookie flags | S | | `HostWebExtensions.cs`, `PacketEventHub.cs`, `GetPacketEvents.cs` | Security 2, 4, 6, 7 |
| 0.B2 | B | Storage model: ADR, then packets as plain Marten documents with `CreatedAt` and `DeletedAt`, true deletes, `CreatedAt` in the list and create responses and the SSE payload; or, if event sourcing is kept, `ArchiveStream` on delete with partitioning and a purge job | M | 0.B1 (same files) | `Domain/`, `Endpoints/`, `Program.cs`, tests, `docs/` | Health decision; Security 1; Performance 6; UX 3 |
| 0.B3 | B | Query and live-update tuning: composite index on user and id (verify the DDL), batched notification dispatch, `Task.WhenAny` heartbeat, exclude the event stream from tracing, spread `PublishToAll` resyncs | S | 0.B2 | `Program.cs`, `PacketNotificationListener.cs`, `GetPacketEvents.cs` | Performance 3, 4, 5, 9 |
| 0.B4 | B | Development authentication scheme (`Authorization: Dev <user>` or `dotnet user-jwts`) selected by the existing policy scheme; AppHost Google parameters optional in Development; a "run without Google" docs section | S | | `HostWebExtensions.cs`, `AppHost.cs`, `docs/` | DX 3 |
| 0.C1 | C | Popup quick fixes: cache capped at the first page, read-then-write in `measureClampedTiles`, animate only live arrivals, one snackbar with no layout shift, `:focus-visible`, signed-out state, rename the image menu item, truncate long tab titles, cap the SSE buffer, full jitter and a delay before the resync fetch | S to M | | `static/`, `popup.ts`, `feed.ts`, `live.ts`, `background.ts`, `sse.ts` | Performance 2, 5; UX 14, 15; Security 10 |
| 0.D1 | D | Mobile quick fixes: cache capped at the first page with debounced writes and no re-parse, route to the feed before Google initialises, `findChildIndexCallback`, ProblemDetails parsing and one `describeError`, clamp long notes, full jitter and a delay before the resync fetch | M | | `packets_provider.dart`, `packet_cache.dart`, `auth_helpers.dart`, `app.dart`, `widgets/` | Performance 1, 5, 8, 10; Health 4; UX 9, 10 |
| 0.F1 | F | Decide vocabulary, ordering and item naming; draft the privacy and terms pages; choose the web app stack and parcel storage | S | | `docs/` | UX 4, 5; overview decisions |

0.B1 to 0.B3 share files, so one person does them in that order; 0.B4 is independent. 0.X lands first and alone. Everything else runs in parallel.

## Phase 1: foundations (three to four weeks)

Goal: a test net under the API, the server-side building blocks every later feature needs, the shared TypeScript core, and the first web app.

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 1.A1 | A | Integration tests: `Testcontainers.PostgreSql` with `WebApplicationFactory`, the test auth scheme, create and list with ownership isolation, paging over 51 packets, delete semantics, SSE end to end through `pg_notify`, schema from an empty database; wired into `atom TestWebApi` | M | 0.A1, 0.B4 | new `Invex.Passel.WebApi.IntegrationTests` | Health 1 |
| 1.A2 | A | Contracts: one wire DTO and one mapper, committed OpenAPI with a staleness test, `contracts/fixtures/` written from the real `JsonOptions` and parsed by both client test suites, `contracts/feed-scenarios.json` run by both feed test suites | M | 0.B2 | `Endpoints/`, `docs/reference/`, both clients' tests | Health 2, 3 |
| 1.A3 | A | Docs: architecture sequence diagram, feed state-machine page, ADRs (storage model, ids, token flow, LISTEN/NOTIFY, tunnel), troubleshooting page, from-zero OAuth walkthrough with `keytool`, release and deploy page | M | 0.B2 | `docs/` | DX 6, 9 |
| 1.A4 | A | `SetupDevEnvironment`: version checks, `devtunnel` login hint, config sync from AppHost user secrets to both `.env` files with one vocabulary, next-steps checklist; keystore written to a random temp path and Gradle without a daemon; keystore secrets required only for the production flavor | M | | `_atom/`, `.env.example` files, `docs/` | DX 8, 10; Health 6; Security 8 |
| 1.B1 | B | First-party sessions: exchange a Google ID token for an access token and a rotating refresh token, revocation per device, API accepts both token kinds during migration | L | 0.A2 | new `Auth` endpoints, `HostWebExtensions.cs` | Features 7 |
| 1.B2 | B | Device registry: register, rename, remove, list; push token storage | M | | new `Devices` endpoints and documents | Features 2 |
| 1.B3 | B | Parcels: `IParcelStore`, Azure Blob with Azurite in the AppHost, upload, download and thumbnail endpoints, the `File` branch of create, quotas, sweep job | L | 0.A2, 0.B2, 0.F1 | new `Parcels` area, `PostPacket.cs`, `AppHost.cs` | Features 1 |
| 1.B4 | B | Link previews: async fetch with SSRF guards, preview fields on the packet, `packet-updated` SSE event | M | 0.B2 | new service, the packet document | Features 5 |
| 1.E1 | E | Shared TypeScript core: npm workspace, `packages/core` from the extension's chrome-free modules with their tests, `packages/ui` from the tokens, `popup.ts` split into feed view, composer and toast modules behind a controller, the popup harness committed; the extension consumes the packages | M | 0.C1 | `src/chrome-extension` moves | Health 8; Web app; DX harness |
| 1.E2 | E | Web app M1: sign-in, read-only live feed, AppHost integration, preview deployment | M | 1.E1 | `apps/web` | Web app |
| 1.E3 | E | Landing page, privacy and terms pages | S | 0.F1 | `apps/web` | Web app |
| 1.C1 | C | Side panel, keyboard commands, omnibox keyword | S to M | 1.E1 | `manifest.json`, `background.ts` | UX 11, 12 |
| 1.C2 | C | Extension unit tests with jsdom and a stubbed `chrome`: auth, api (401 matrix, problem parsing), live backoff, background delete scheduling; coverage in the Atom target | M | 1.E1 | `test/` | Health 9 |
| 1.D1 | D | Android share target and process-text, pre-filled send sheet | S to M | 0.F1 | `AndroidManifest.xml`, `pubspec.yaml`, new handler | UX 1 |
| 1.D2 | D | Settings page skeleton: account, version, theme override | S | | new `settings/` | UX 18 |
| 1.D3 | D | Dependency majors: Riverpod 3 (automated migration), `share_plus` 13, `dynamic_color` 2 | M | 0.D1 | `pubspec.yaml`, `lib/` | Health 7 |
| 1.D4 | D | Widget tests: feed page, list load-more and retry and signed-out rows, send sheet, tiles; coverage in the Atom target | M | 0.D1 | `test/` | Health 9 |
| 1.F1 | F | Tile redesign specification and brand kit (logo, wordmark, icon style, empty-state illustrations) | M | 0.F1 | design assets, `docs/` | UI |

1.B1 to 1.B4 all add to `src/webapi`, mostly in new folders; run them on separate branches and expect small merge conflicts in `Program.cs` and `EndpointMapper.cs`. 1.E1 must land before 1.E2, 1.C1 and 1.C2. 1.D3 and 1.D4 touch `lib/` and `test/` broadly, so land them before 1.D1 and 1.D2 or rebase. Lane A and lane F packages are independent of everything else in the phase.

## Phase 2: parity and parcels (four to six weeks)

Goal: every client gets the redesign, parcels and notifications; the web app reaches parity; the apps are listed.

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 2.B1 | B | Push sender: FCM and Web Push, triggered on packet commit, targeting the user's other devices | M | 1.B2 | new service beside the notification listener | Features 2 |
| 2.B2 | B | Search: `?q=` on the list endpoint with a Marten full-text index | M | 0.B2 | `GetPackets.cs`, `Program.cs` | Features 6 |
| 2.B3 | B | Account deletion and export, retention setting with nightly expiry, trash and restore | M | 0.B2 | new endpoints and jobs | Features 10, 11, 14; Security 1 |
| 2.B4 | B | Per-user change log, resumable SSE with event ids, `?since=` delta sync | M | 0.B2, 1.A2 | the packet document, `GetPacketEvents.cs`, both clients' stores | Health 3 |
| 2.C1 | C | Tile redesign, day headers, previews, avatar menu in the extension | M | 0.B2, 1.B4, 1.F1 | `popup/`, `packages/ui` | UI |
| 2.C2 | C | Parcels in the extension: paste, drop, optional host permission for image URLs, thumbnails, viewer | M | 1.B3, 2.C1 | `popup/`, `background.ts`, `manifest.json` | Features 1 |
| 2.C3 | C | Sessions in the extension | S | 1.B1 | `auth.ts`, `api.ts` | Features 7 |
| 2.D1 | D | Tile redesign, detail page, day headers, surface-coloured app bar, one ordering | M | 0.B2, 1.B4, 1.F1 | `widgets/`, `theme/` | UI; UX 4 |
| 2.D2 | D | Parcels on mobile: shared images, picker, camera, viewer, authenticated cache | M | 1.B3, 1.D1, 2.D1 | `packets/data/`, `widgets/` | Features 1 |
| 2.D3 | D | Push notifications and device registration | M | 1.B2, 2.B1 | new `push/`, `auth/` | Features 2 |
| 2.D4 | D | Sessions, account deletion and export in settings | M | 1.B1, 2.B3, 1.D2 | `auth/`, `settings/` | Features 7, 14 |
| 2.E1 | E | Web app M2 parity: send, delete with undo, offline cache, PWA install, the mobile layout below 600 px and the two-pane layout above 840 px, settings | L | 1.E2, 0.B2, 0.F1 | `apps/web` | Web app |
| 2.E2 | E | Web app parcels, share target, Web Push, search | M | 1.B3, 2.B1, 2.B2 | `apps/web` | Web app |
| 2.A1 | A | Release pipeline and store listings: extension zip with the version stamped from GitVersion to the Chrome Web Store, app bundle to Play internal testing then production, deletion page linked from the listing | M | 0.A1, 1.E3, 2.B3 | `_atom/`, `.github/` | DX 1 |
| 2.A2 | A | Observability in production: dashboards, alerts on error rate and stream count, error tracking; a load test with a few hundred synthetic users against staging | M | 0.A2 | infrastructure, `tests/load/` | Performance steps |

Within a lane the packages touch different screens and can run in parallel branches; the redesign packages (2.C1, 2.D1) conflict with the parcel packages (2.C2, 2.D2) in the tile widgets, so land the redesign first.

## Phase 3: delight (ongoing)

In no particular order, each independent unless noted: send to a specific device (needs 1.B2 and 2.B1), pin and archive, home-screen widget and quick-settings tile, app shortcuts, Android App Links for `passel.link/p/{id}`, collections (needs parcels), offline send queue, read state, Markdown and code notes, public API with keys and a CLI (needs 1.B1), webhooks, iOS with Sign in with Apple, a desktop app, an end-to-end encryption design study.

## Critical paths

- **Ship:** 0.X, 0.A1, 0.A2, 0.A3, with 0.B1 and 0.B2 landing before the first public deployment.
- **Parcels:** 0.B2, 1.B3, then 2.C2, 2.D2 and 2.E2 in parallel.
- **Design:** 0.F1, 0.B2, 1.F1, then 2.C1, 2.D1 and 2.E1 in parallel.
- **Web app:** 0.C1, 1.E1, 1.E2, 2.E1; public launch also wants 1.B1 (so the web session doesn't expire hourly) and 2.A1.
- **Notifications:** 1.B2, 2.B1, then 2.D3 and 2.E2.
- **Store listings:** 0.F1, 1.E3, 1.D2, 2.B3, 2.D4, 2.A1.

## What can run at the same time

| While working on | Safe to run in parallel | Will conflict |
|---|---|---|
| Any lane B package | Every A, C, D, E, F package | Other lane B packages that edit `Program.cs` or `EndpointMapper.cs` (rebase; conflicts are small) |
| 1.E1 (core extraction) | Lanes A, B, D, F | Every lane C package: do 1.E1 first, or rebase C onto it |
| 2.C1 or 2.D1 (redesign) | Everything outside their lane | 2.C2 or 2.D2 in the tile widgets: land the redesign first |
| Lane D | Everything else | Nothing: the mobile app shares no files with the other lanes |
| Lane A docs and tests | Everything | 1.A2 edits both clients' test folders lightly; coordinate with 1.C2 and 1.D4 |
| Lane F | Everything | Nothing, but its decisions gate 1.B3, 1.D1, 1.E3, 2.C1, 2.D1, 2.E1 |

Phase 0's 0.X is the exception: it touches every area lightly and should be merged before anything else starts.

## If one person does it all

Quick wins first, then ship, then the thing users notice most, then the web app, then parcels: 0.X, 0.F1, 0.D1, 0.C1, then 0.B1, 0.B2, 0.B3, 0.B4, then 0.A1, 0.A2, 0.A3, 0.A4, then 1.D1, then 1.A1, 1.E1, 1.E2, 1.E3, then 1.B3 followed by 2.C1, 2.C2, 2.D1, 2.D2, then 1.B2, 2.B1, 2.D3, then 1.B1, 2.C3, 2.D4, then 2.E1, 2.B3, 2.A1, and the rest as demand dictates.
