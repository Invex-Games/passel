# Contributing

Thanks for working on Passel. This page covers the conventions; the developer docs in `docs/` cover the setup ([Prerequisites](docs/getting-started/prerequisites.md), [Running Locally](docs/getting-started/running-locally.md)) and the build ([Build Targets](docs/reference/build-targets.md)).

## Getting set up

```
dotnet tool restore
dotnet atom SetupDevEnvironment
aspire run
```

`SetupDevEnvironment` checks the tools, creates `src/mobile_app/.env` from its example and restores the npm and Flutter packages. The AppHost asks for the Google OAuth client values on its first run; see [Google OAuth Clients](docs/getting-started/google-oauth.md).

## Branches and commits

Versions come from commit messages through GitVersion (`GitVersion.yml`), so the conventions matter:

| Prefix | Example | Effect on the next version |
|---|---|---|
| `feat` | `feat(mobile): share target` | Minor bump |
| `fix` | `fix(webapi): send notifications after the commit` | Patch bump |
| `breaking` | `breaking(api): drop the v1 packets endpoint` | Major bump |
| `chore`, `docs`, `build`, `ci`, `refactor`, `test`, `perf` | `chore: pin the Atom tool` | None |

Scopes are the component: `webapi`, `apphost`, `core`, `ui`, `extension`, `mobile`, `atom`, `docs`. Branches are named after the prefix: `feat/share-target`, `fix/token-refresh`, `chore/ci`. Pull requests target `main` and should be squash-merged with the conventional title, or rebased when every commit is conventional on its own.

## Before opening a pull request

- Run `dotnet atom Test`, or the suite for the component you changed:
  - `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/Invex.Passel.WebApi.Tests.csproj`, and with Docker running, `dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj`
  - `npm run typecheck && npm test` in `src` (the npm workspace: the shared packages and the extension)
  - `flutter analyze && flutter test` in `src/mobile_app`
- Warnings are errors in every component; keep it that way rather than suppressing.
- Update the docs page for anything you changed in behaviour, configuration or commands, and preview with `dotnet atom ServeDocs`.
- If you changed `_atom/IBuild.cs`, run `dotnet atom Gen` and commit the regenerated files under `.github/`. A CI run fails early when they are out of date.
- Add a line to `CHANGELOG.md` under "Unreleased" for anything a user or operator would notice.

## Continuous integration

Every pull request runs the `Validate` workflow (all three test suites and a docs build); every push to `main` runs `Build` (tests, release builds of the API and the extension, and publishing the docs site). Both are generated from `_atom/IBuild.cs`; see [Continuous Integration](docs/guides/ci.md) for what the repository needs configured.

## Local configuration files

`src/chrome-extension/.env` and `src/mobile_app/.env` hold your Google client ids and API URLs and are git-ignored; copy them from the `.env.example` next to each. The AppHost supplies the API and the extension's watch build with its own values, so the extension `.env` only matters for standalone builds. Never commit a real client secret, keystore or token; the ignore rules cover the usual files, but check `git status` before committing.

## Dependencies

NuGet versions live in `Directory.Packages.props` (central package management); npm and pub versions in their lock files. Dependabot opens one grouped pull request per package manager every week. Upgrade majors in their own pull request with the tests green.
