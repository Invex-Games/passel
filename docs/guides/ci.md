# Continuous Integration

The workflows under `.github/workflows/` and the Dependabot configuration in `.github/dependabot.yml` are generated from `_atom/IBuild.cs` by the [Atom GitHub workflows module](https://github.com/Invex-Games/atom). Don't edit the YAML by hand: change the definitions and run `dotnet atom Gen`. Running any target locally in interactive mode regenerates the files too, and a headless run (CI) fails early if the committed files are out of date.

On Windows the generator writes CRLF line endings, so after an interactive run git lists the files under `.github/` as modified although only the line endings differ. `git checkout -- .github` puts them back, and a headless run never rewrites them.

## Workflows

| Workflow | Runs on | Jobs |
|---|---|---|
| `Validate` | Pull requests into `main`, manual dispatch | `TestWebApi`, `TestChromeExtension`, `TestMobileApp` (its coverage report attached to the run as `MobileAppCoverage`) and `BuildDocs`, in parallel; nothing is published |
| `Build` | Pushes to `main`, manual dispatch | The three test suites (the mobile app's coverage report attached to the run), `BuildWebApi` and `BuildChromeExtension` (artifacts attached to the run), `BuildDocs` and `PublishDocs` (the site force-pushed to the `gh-pages` branch) |
| Dependabot | Weekly, Monday 06:00 Brisbane time | One grouped pull request each for NuGet, npm, pub and GitHub Actions, with `build(...)` commit prefixes so versions don't bump |

A newer run of a workflow on the same branch cancels the one still in progress. Every job checks the repository out and sets up the .NET 10 SDK; Node and Docker come with the runner (`TestWebApi` starts PostgreSQL in a container for the integration tests), and `TestMobileApp` installs the Flutter version pinned in `_atom/appsettings.json` into `.flutter/` because runners don't have one.

The mobile app's release build (`BuildMobileAppAndroid`) is not in a workflow yet: it needs the Android keystore and client ids from Azure Key Vault, so the repository first needs an Azure identity with access to the vault.

## What the repository needs

| Setting | Where | Used by |
|---|---|---|
| `CHROME_EXTENSION_CLIENT_ID` | Settings, Secrets and variables, Actions, Variables | `BuildChromeExtension`, which otherwise has no Google client id outside a developer's `.env` |
| `GITHUB_TOKEN` | Provided by GitHub Actions | `PublishDocs`, which needs `contents: write` on the job (already declared) |
| `AZURE_VAULT_ADDRESS`, `AZURE_VAULT_TENANT_ID`, `AZURE_VAULT_APP_ID` (variables) and `AZURE_VAULT_APP_SECRET` (secret) | Same pages | Only targets that read Key Vault secrets; the current workflows don't, so these can stay unset until the mobile release build is added |
| GitHub Pages source | Settings, Pages: deploy from the `gh-pages` branch | Serving the published docs site |
| Branch protection on `main` | Settings, Branches | Require the `Validate` jobs to pass and at least one review before merging |

## Running a workflow's target locally

Each job runs one target in headless mode, for example:

```
dotnet atom TestWebApi --headless
```

`--headless` makes the build fail instead of prompting for anything, which is also a quick way to find a target that would hang in CI.
