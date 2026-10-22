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

### Changed

- Marten 9 and current Aspire, OpenTelemetry and Google packages.
- Line endings are LF everywhere, files carry no byte-order mark, and the Atom build tool is pinned in `dotnet-tools.json`.
- NuGet package versions are managed centrally in `Directory.Packages.props`; the .NET SDK is pinned in `global.json`.
- The extension tests run on vitest 5; the unused `cupertino_icons` dependency and the unused file-packet command and endpoint helpers were removed.

### Fixed

- Packets are scoped to their owner on every endpoint, and ids cannot be probed.
- Notifications are sent only after the database commit, so a rolled-back packet is never announced.
- The extension routes every API call through its service worker and keeps the ID token in session storage.
- The mobile app refreshes tokens, clears user data on sign-out and account change, and no longer races its feed updates.
- Build fixes: the extension is built against its packets endpoint, the pub cache survives Flutter builds, and tools are found on any OS.
