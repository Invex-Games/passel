# Developer Experience

Scope: what it takes to go from clone to a running system, to a passing test run, and to a release, on the machine a developer actually has. Method: every documented command was run from the repository root with warm caches, the onboarding docs were checked claim by claim against the repository, and the Atom build, the ignore rules and line endings were inspected. The AppHost itself and the `atom Build*` targets were read rather than run.

## Verdict

The inner loop is excellent: one build, three test suites, zero warnings, all green in seconds, with docs that are structured, honest and precise, an AppHost that provisions the database, the extension's watch build and a phone-reachable tunnel, and a `ConfigErrorPage` that tells a mobile developer exactly which value is missing. The outer loop is missing: there is no CI, no deployment path, no release automation and no contribution conventions. And the first ten minutes for a stranger end at a Google Cloud console, because nothing in the system can authenticate without three real OAuth clients.

Score: 4 / 5 for the inner loop, 1 / 5 for the outer loop.

## The developer loop as measured

| Command | Result | Time (warm) | Notes |
|---|---|---|---|
| `dotnet build Invex.Passel.slnx -c Debug` | 5 projects, 0 warnings, 0 errors | 7.2 s | `TreatWarningsAsErrors` holds |
| `dotnet test --project src/webapi/Invex.Passel.WebApi.Tests/…` | 42 passed | 4.0 s | Needs `--project` under Microsoft.Testing.Platform; docs say so |
| `npm run typecheck` (extension) | clean | 0.85 s | |
| `npm test` (extension) | 39 passed | 1.0 s | |
| `flutter analyze` | no issues | 22.4 s | The slowest step |
| `flutter test` | 170 passed | 4.2 s | |
| `dotnet list package --vulnerable --include-transitive` (5 projects) | none vulnerable | 1 to 9 s each | |
| `npm audit` | 4 advisories, all dev-only (vitest 3 chain, esbuild) | 2 s | `vitest@5` and `npm audit fix` clear them |
| `flutter pub outdated` | 7 direct outdated, 3 majors (`flutter_riverpod` 3, `share_plus` 13, `dynamic_color` 2) | 1.8 s | `google_sign_in` current |
| `atom -h` (as the README says) | prints the global tool wrapper's own usage, not the targets | | `dotnet run --project _atom -- --help` lists all ten targets |
| `dotnet run --project src/webapi/Invex.Passel.WebApi` (API alone) | fails: `ConnectionString is missing` | | Clear message; there is no way to run the API without PostgreSQL and Google settings |

Tool versions on this machine: .NET SDK 10.0.401, Node 24, Flutter 3.41.0, Docker 29, Aspire CLI 13.6.2, Dev Tunnels 1.0, Invex.Atom.Tool 4.2.0 globally against Invex.Atom.Build 3.7.0 in `_atom` (5.0.0 is current).

## Strengths

- Documentation far above the solo-project norm: getting started, components, guides and reference; honest about limits; precise about ports, precedence and SSE semantics; already a searchable DocFX site. Every port, endpoint, limit and precedence rule the docs state was checked and is correct.
- The AppHost removes most setup pain: PostgreSQL with pgweb, the extension's watch build with injected configuration, a stable tunnel for phones, parameter prompting persisted to user secrets, telemetry to the dashboard.
- `SetupDevEnvironment` is idempotent and prints an install hint for each missing tool; `ConfigErrorPage` names the missing dart-define and the exact command to run.
- Fast, green, warning-free verification, so a newcomer's first change is judged by the same bar as the existing code.
- `.env.example` files with real comments for both clients.

## Findings

1. **There is no deployment or release path (High).** `_atom/appsettings.json` names `https://api.passel.link`, but `BuildWebApi` only publishes to a folder; there is no container image, compose file, Bicep, Terraform or `azd` definition; Marten applies schema at startup with the application's credentials; the production values for the connection string, the Google settings, the Sqids alphabet and the telemetry endpoint have no documented source; the extension's `version` is a hard-coded "1.0.0" while .NET versions come from GitVersion; `BuildChromeExtension` makes a zip with no Web Store step; `BuildMobileAppAndroid` makes a bundle with no Play step. Fix, the minimum repeatable path: `dotnet publish /t:PublishContainer` with tags from GitVersion pushed to a registry; one host (a container app or a small VM with compose) with managed PostgreSQL; `AutoCreate.None` in production and schema applied at deploy with the JasperFx command line or a one-shot job; secrets from the host's store as environment variables with the keys documented; a `DeployWebApi` Atom target and workflow; `version` stamped into `dist/manifest.json` from GitVersion and published with `chrome-webstore-upload-cli`; Gradle Play Publisher or `fastlane supply` for the bundle and `mapping.txt`.

2. **No CI, no contribution conventions, no governance files (High).** No `.github/`, no pull-request or issue templates, no CODEOWNERS, CONTRIBUTING, CHANGELOG or LICENSE (nothing says whether the code may be used at all), yet `GitVersion.yml` encodes conventional-commit bump rules and `feat/`, `fix/`, `chore/` branch names that the history already follows. Fix: generate the workflows with Atom's GitHub workflows module (`Test` on pull requests, `Build*` and docs publish on `main`, a Dependabot definition), add CONTRIBUTING.md (branch and commit conventions and their version effect, `atom Test` before a pull request, how the `.env` files work), a LICENSE, a one-line CODEOWNERS, a pull-request template, and a CHANGELOG generated from conventional commits.

3. **The first ten minutes end at a Google Cloud console (High).** Every packet endpoint requires a Google ID token, the AppHost prompts for three OAuth values, Scalar's login needs the web client secret, and there is no development scheme, seeded user or fake issuer. A contributor without access to the maintainer's Cloud project must create a project and three clients (web with secret, extension, Android with their debug SHA-1) before a single request succeeds. Fix: a Development-only authentication scheme selected by the existing policy scheme (`Authorization: Dev <user-id>`, or tokens from `dotnet user-jwts`) so Scalar, curl and the integration tests work with the Google parameters empty; make the three AppHost parameters optional in Development; add a "run without Google" section to the running-locally page and keep the OAuth page for real sign-in.

4. **Line endings, BOMs and no `.gitattributes` (Medium).** The index is 100 percent LF, the worktree is 42 percent CRLF because `core.autocrlf=true`, `.editorconfig` demands CRLF for every file, one file has mixed endings, 46 tracked files start with a BOM and the rest don't, and git warns on every status and diff. Fix with zero churn: `.gitattributes` with `* text=auto eol=lf` plus binary rules for `*.png` and `*.psd`, `.editorconfig` `end_of_line = lf`, `git add --renormalize .` (a no-op for the index), and let the IDE strip the BOMs.

5. **The documented target discovery doesn't work, and tool versions float (Medium).** `atom -h` with the installed 4.2.0 global tool prints the wrapper's usage; `atom -- -h` errors; the tool is not resolvable from Git Bash at all because the shim is `atom.cmd`. `_atom` is on 3.7.0, the global tool is 4.2.0, 5.0.0 is current, and there is no tool manifest. Fix: a `dotnet-tools.json` manifest so `dotnet tool restore` and `dotnet atom <Target>` give everyone the same version in every shell; fix the README and the build-targets page; upgrade `_atom` to the current Atom in its own change.

6. **Google OAuth setup is incomplete for a stranger (Medium).** The OAuth page has no consent-screen or test-users step, no `keytool` command for the debug SHA-1, doesn't say that each developer's debug keystore SHA-1 must be added to the one Android client, names no one to "ask for the existing ids", and doesn't explain why `APP_CLIENT_ID` is optional when the Android client must still exist. Fix: a numbered from-zero walkthrough with the `keytool` command, the consent screen, the per-developer SHA-1 note, and a short "if sign-in fails with X, check Y" table (redirect URI mismatch for the extension id, missing SHA-1 giving `DEVELOPER_ERROR` on Android, audience not in `AdditionalAudiences` giving 401).

7. **Cross-platform friction (Medium).** `atom.cmd` is invisible to bash; the npm `clean` and `package` scripts require PowerShell; the uncommitted launch configuration uses a backslash path; Atom helpers branch on Windows with `where`/`which` and `PATH`-string heuristics; the loop has never been recorded on macOS or Linux. Fix: the tool manifest (finding 5), Node-only npm scripts, a note about the `.cmd` shim in prerequisites, and one run of the loop on macOS or Linux written into the docs.

8. **`SetupDevEnvironment` under-checks and under-automates (Low).** It probes five tools, Docker running and the dev certificate, but not the .NET SDK version, Node version, JDK or Android SDK, `git`, or whether `devtunnel` is logged in; it creates `src/mobile_app/.env` from the example and leaves every value to be typed by hand. Fix: version checks, a `devtunnel user show` hint, the configuration sync from [Codebase Health](codebase-health.md) finding 6, and a printed next-steps checklist.

9. **Documentation gaps (Low, specific).** No architecture diagram (one Mermaid sequence diagram for create, `pg_notify`, `LISTEN`, hub, SSE, client reconcile would do; the modern DocFX template renders Mermaid), no page on how the feed state machine works (its invariants exist only as comments in `feed.ts` and `packets_provider.dart`), no ADRs (event sourcing for a two-event aggregate, Sqids ids, the implicit `id_token` flow, LISTEN/NOTIFY over SignalR or Redis, tunnel over emulator), no troubleshooting page (Docker not running, dev certificate untrusted, `devtunnel` not logged in, extension id and redirect mismatch, port 7030 busy, `atom` not found in bash), no release or deploy page, and the HTTP API page restates what a committed OpenAPI document could carry.

10. **A debug build through Atom appears to need Key Vault secrets (Low, unverified by execution).** `BuildMobileAppAndroid` declares the keystore alias and password as required parameters although they are only used for the production flavor, so a development APK through Atom needs Key Vault access while the docs say the secrets are for production. Fix: require them only when the flavor is production, and document `flutter run --flavor development --dart-define-from-file=.env` as the day-to-day path.

11. **Repository noise and sharing (Low).** A real signing keystore sits in the repository root (ignored, never committed, still one `git add -f` from history; Key Vault already holds it); `devtools_options.yaml` is untracked and un-ignored; the generated `.flutter-plugins` is tracked, stale and machine-specific; the 14 KB `.gitignore` concatenates the Visual Studio, JetBrains and Flutter-SDK templates (so `version` and `*.lock` are ignored everywhere and `pubspec.lock` survives only through a nested re-include); `.idea/` is fully ignored so Rider run configurations can't be shared; `.vscode/launch.json` has a personal device id pending. Fix: move the keystore out, commit `devtools_options.yaml`, untrack `.flutter-plugins`, trim the ignore file to what the repo needs, add shared launchers (`tasks.json` for `atom Test` and `aspire run`, allow the Rider run-configuration folder through the ignore rules), and don't commit the device id.

12. **Version pinning is partial (Low).** Flutter 3.41.0 is pinned for release builds only and the build may switch a developer's own SDK; `TestMobileApp` uses whatever Flutter is on `PATH`; no SDK pin in `global.json`; no Node pin. Fix: `global.json` with `rollForward: latestFeature`, `engines` in `package.json`, FVM or a warning when the `PATH` Flutter differs from the pinned version.

## A small tool that fell out of this review

The extension popup was reviewed by running the real `popup.js` bundle in a plain browser tab with a 60-line stub of the `chrome.*` APIs (`runtime.sendMessage` and `connect`, `storage`, `tabs`) returning canned packets. It renders every state (signed out, empty, feed, copy menu, undo toast, dark mode) without loading the extension or signing in, reloads instantly, and would make a fine `npm run harness` for popup work and for screenshot tests. It lives in this session's scratch folder; it is worth adding to `src/chrome-extension/harness/`.

## Steps forward

1. **CI through Atom's workflow generator, with Dependabot and the governance files** (M). Findings 2 and 12.
2. **Deploy the API repeatably and stamp versions into every artifact** (L). Finding 1.
3. **Development auth scheme and optional AppHost parameters** (S). Finding 3; also what the integration tests need.
4. **Tool manifest, `.gitattributes`, LF, no BOMs, fix the README** (S). Findings 4, 5, 7.
5. **OAuth walkthrough, troubleshooting page, architecture diagram, feed state-machine page, ADRs** (M). Findings 6 and 9.
6. **`SetupDevEnvironment` version checks and config sync; keystore out of the tree; ignore-file trim** (S to M). Findings 8, 10, 11.
7. **Popup harness in the repo** (S). Useful for the redesign work in [UI Design](ui-design.md).
