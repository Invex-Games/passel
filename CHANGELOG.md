# Changelog

All notable changes to Passel. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions come from [GitVersion](GitVersion.yml) and conventional commits. Nothing has been released yet.

## Unreleased

### Added

- The Android app is a share target: "Send to Passel" appears in the share sheet for text and links and in the text selection menu, and opens the send sheet filled in. The extension's screens use the same Send, link and note wording as the app.
- Real-time packet updates: the API streams server-sent events backed by PostgreSQL notifications, and the mobile app and extension keep a live connection open while visible.
- Deleting packets, with a four-second undo, in the mobile app and the extension; deletions are announced live.
- The extension popup shows the packet feed, with paging, caching and copy actions.
- An Aspire AppHost that runs PostgreSQL, the API, the extension's watch build and a dev tunnel for phones, prompting for the Google OAuth values.
- Build targets for the test suites and for checking and preparing a development machine (`SetupDevEnvironment`).
- A DocFX developer site under `docs/`, with an October 2026 evaluation of the project, a feature list, a web app plan and a roadmap.
- GitHub Actions workflows and Dependabot configuration generated from the Atom build definition, plus contributing guidelines, code owners, a pull request template and the MIT license.
- Abuse controls in the API: rate limits per user for creates, reads and event streams, per-user packet and storage quotas, caps on open event streams per user and per instance, and streams that end when the token they were opened with expires.
- A Development-only `Authorization: Dev <user>` scheme, so the API, Scalar and the tests run without Google credentials; the AppHost's Google parameters are optional in Development. Scalar opens with the scheme filled in for a visitor who is not logged in with Google, and otherwise selects the Google login, which the document declares as its own scheme.
- Sign-in without Google in the clients' development builds: the mobile app's `DEV_USER` dart-define and the extension's `DEV_SIGN_IN` build flag (set by the AppHost) sign in through that scheme as any named user, with a field for switching users, so per-user behaviour can be tested from one device. Production builds refuse the define and never compile the flag in.
- A USB path for mobile development: the development flavor of the app may use plain HTTP to `localhost` and the emulator host, so a phone reaches the local API through `adb reverse` without a tunnel; the production flavor refuses an `http://` API URL. A `TUNNEL_ACCESS_TOKEN` dart-define and a `DevTunnel:AllowAnonymous` AppHost setting for running the dev tunnel without anonymous access.
- Integration tests for the API against PostgreSQL in Docker (Testcontainers): ownership isolation, paging, delete semantics, live updates end to end and the schema on an empty database. `dotnet atom TestWebApi` and the workflows run them after the unit tests.

### Changed

- The mobile feed shows the newest item at the top like the extension, with pull-to-refresh at the top and older items loading at the bottom, and its screens say "Send" and speak of links, notes and files rather than packets (ADR 0002, which also settles the web app stack, the parcel store and the hosting).
- Packets are plain Marten documents instead of event streams with a snapshot (ADR 0001): a delete erases the packet, each packet is stored once, and the list and create responses and the `packet-created` event carry `createdAt`. A development database from before keeps its old tables, which can be dropped.
- Marten 9 and current Aspire, OpenTelemetry and Google packages.
- Line endings are LF everywhere, files carry no byte-order mark, and the Atom build tool is pinned in `dotnet-tools.json`.
- NuGet package versions are managed centrally in `Directory.Packages.props`; the .NET SDK is pinned in `global.json`.
- The extension tests run on vitest 5; the unused `cupertino_icons` dependency and the unused file-packet command and endpoint helpers were removed.
- HTTP hygiene in the API: CORS is off until the web app needs it, HSTS and host filtering apply outside Development, forwarded headers are honoured behind a setting, the development cookies and OIDC nonce validation are hardened, and the Aspire dashboard asks for its login token.
- The dev tunnel no longer starts with the AppHost; start it from the dashboard when a phone is not on USB. Scalar, the OpenAPI document and the browser login answer only on `localhost`, so they are never served through the tunnel.
- The extension caches only the first page of the feed, lays the feed out in one pass and animates only packets that arrive live, shows every piece of feedback in one snackbar, hides the composer while signed out, truncates long tab titles, caps its event-stream buffer, and spreads reconnects and resync fetches.
- The mobile app caches only the first page with debounced writes and a single parse, shows the cached feed before Google Sign-In has initialised, keeps tile state across inserts, clamps long notes to eight lines with a "Show more" toggle, spreads reconnects and resync fetches, and keeps its app bar while the feed is pulled to refresh, with the empty feed saying which way to pull.
- Query and live-update tuning in the API: the feed is served by one composite index on user and packet id (with the DDL and measured plans in the Web API docs), live updates are dispatched in batches with one query per batch, stream heartbeats are timed without exceptions, event streams are left out of request tracing, resyncs after a listener reconnect are spread over a few seconds, and each stream sends its own `retry` value.
- The mobile app runs on Riverpod 3 (`flutter_riverpod` 3.3) and `share_plus` 13, with its providers opted out of the automatic retry Riverpod 3 adds so failures still surface at once; `dynamic_color` stays on 1.x until the Flutter SDK ships Dart 3.12.

### Fixed

- Packets are scoped to their owner on every endpoint, and ids cannot be probed.
- Notifications are sent only after the database commit, so a rolled-back packet is never announced.
- The extension routes every API call through its service worker and keeps the ID token in session storage.
- The mobile app refreshes tokens, clears user data on sign-out and account change, and no longer races its feed updates.
- The mobile app shows readable messages for API, network and sign-in failures instead of raw exception text.
- Build fixes: the extension is built against its packets endpoint, the pub cache survives Flutter builds, and tools are found on any OS.
