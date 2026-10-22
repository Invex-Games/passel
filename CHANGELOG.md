# Changelog

All notable changes to Passel. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions come from [GitVersion](GitVersion.yml) and conventional commits. Nothing has been released yet.

## Unreleased

### Added

- Real-time packet updates: the API streams server-sent events backed by PostgreSQL notifications, and the mobile app and extension keep a live connection open while visible.
- Deleting packets, with a four-second undo, in the mobile app and the extension; deletions are announced live.
- The extension popup shows the packet feed, with paging, caching and copy actions.
- An Aspire AppHost that runs PostgreSQL, the API, the extension's watch build and a dev tunnel for phones, prompting for the Google OAuth values.
- Build targets for the test suites and for checking and preparing a development machine (`SetupDevEnvironment`).
- A DocFX developer site under `docs/`, with an October 2026 evaluation of the project, a feature list, a web app plan and a roadmap.
- GitHub Actions workflows and Dependabot configuration generated from the Atom build definition, plus contributing guidelines, code owners and a pull request template.
- Abuse controls in the API: rate limits per user for creates, reads and event streams, per-user packet and storage quotas, caps on open event streams per user and per instance, and streams that end when the token they were opened with expires.
- A Development-only `Authorization: Dev <user>` scheme, so the API, Scalar and the tests run without Google credentials; the AppHost's Google parameters are optional in Development.

### Changed

- Marten 9 and current Aspire, OpenTelemetry and Google packages.
- Line endings are LF everywhere, files carry no byte-order mark, and the Atom build tool is pinned in `dotnet-tools.json`.
- NuGet package versions are managed centrally in `Directory.Packages.props`; the .NET SDK is pinned in `global.json`.
- The extension tests run on vitest 5; the unused `cupertino_icons` dependency and the unused file-packet command and endpoint helpers were removed.
- HTTP hygiene in the API: CORS is off until the web app needs it, HSTS and host filtering apply outside Development, forwarded headers are honoured behind a setting, the development cookies and OIDC nonce validation are hardened, and the Aspire dashboard asks for its login token.
- The extension caches only the first page of the feed, lays the feed out in one pass and animates only packets that arrive live, shows every piece of feedback in one snackbar, hides the composer while signed out, truncates long tab titles, caps its event-stream buffer, and spreads reconnects and resync fetches.
- The mobile app caches only the first page with debounced writes and a single parse, shows the cached feed before Google Sign-In has initialised, keeps tile state across inserts, clamps long notes to eight lines with a "Show more" toggle, and spreads reconnects and resync fetches.

### Fixed

- Packets are scoped to their owner on every endpoint, and ids cannot be probed.
- Notifications are sent only after the database commit, so a rolled-back packet is never announced.
- The extension routes every API call through its service worker and keeps the ID token in session storage.
- The mobile app refreshes tokens, clears user data on sign-out and account change, and no longer races its feed updates.
- The mobile app shows readable messages for API, network and sign-in failures instead of raw exception text.
- Build fixes: the extension is built against its packets endpoint, the pub cache survives Flutter builds, and tools are found on any OS.
