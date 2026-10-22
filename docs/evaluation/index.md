# Evaluation

An end-to-end evaluation of Passel as of October 2026: the API, the Chrome extension, the Android app, the build and the docs. Each chapter is a report of findings with concrete steps forward; the last three chapters turn the findings into a feature list, a web app plan and a roadmap cut into parallel work packages.

How it was done: every source file was read; three independent review passes (security, performance, codebase health with developer experience) each produced evidence-backed findings and ran the real tooling (builds, all three test suites, dependency and vulnerability scans, the extension's production build); the extension popup was rendered from its real bundle against a stubbed `chrome.*` API in every state and screenshotted; the mobile app was reviewed from source. Numbers that could not be measured without a running database are marked as estimates in the chapters.

## Scorecard

| Dimension | Score | In one line | Chapter |
|---|---|---|---|
| Performance | 3 / 5 | Nothing is slow today; both clients persist the whole feed per change, the list query has no matching index, and the event store grows forever | [Performance](performance.md) |
| Security | 4 / 5 exposure, 2 / 5 abuse and privacy | Auth is correct and fails closed; no quotas, deletes never erase, no account deletion, the dev tunnel is public | [Security](security.md) |
| User experience | 3 / 5 simple, 2 / 5 advanced | The core loop is careful; no share sheet, no notifications, no timestamps or search, and the clients disagree on ordering and words | [User Experience](ux.md) |
| UI design | 3 / 5 | Consistent Material 3 in light and dark; the tile is heavy and uninformative, links are raw URLs | [UI Design](ui-design.md) |
| Codebase health | 3 / 5 | Small, strict, readable, well commented; no integration tests, the wire model and the feed logic are duplicated without drift detection | [Codebase Health](codebase-health.md) |
| Developer experience | 4 / 5 inner loop, 1 / 5 outer loop | Build and tests green in seconds with excellent docs; no CI, no deployment, and a stranger can't authenticate without a Google Cloud project | [Developer Experience](developer-experience.md) |

## What to do first

Five things cut across every chapter and unblock the rest:

1. **CI and a repeatable deployment.** Nothing reaches `api.passel.link` today and nothing checks a pull request. Atom can generate the workflows.
2. **Decide the storage model for packets.** Event sourcing with nothing reading the history causes the delete-never-erases problem, the unbounded event table and the extra round trips. Plain documents are recommended; decide before parcels and previews add more events.
3. **Abuse controls.** A rate limiter, per-user quotas, caps on event streams and streams that end with the token.
4. **Bound both client caches and add `CreatedAt` to the API.** The two cheapest fixes with the largest effect on performance and on the tile redesign.
5. **An Android share target.** The single largest gap in the product's core job.

## Decisions the team needs to make

The chapters compare options where there is no obvious pick; these are the ones that gate other work.

| Decision | Options compared in | Recommendation |
|---|---|---|
| Storage model for packets | [Codebase Health](codebase-health.md) | Plain Marten documents, with a per-user change log when resumable sync is wanted |
| Vocabulary and feed ordering | [User Experience](ux.md) | "Send" everywhere, user-facing nouns, newest at the top on every client |
| Parcel storage | [Feature Ideas](features.md) | Azure Blob behind an `IParcelStore` interface, Azurite locally |
| Sessions | [Feature Ideas](features.md) | Exchange Google ID tokens for first-party access and refresh tokens |
| Web app stack | [Web App Plan](web-app.md) | A TypeScript progressive web app sharing a core package with the extension |
| UI component approach | [UI Design](ui-design.md) | Keep the hand-rolled MD3 tokens, grow them into a shared `@passel/ui` package |
| Dev tunnel | [Security](security.md) | `adb reverse` by default, tunnel token when remote, explicit start now |

## Chapters

- [Performance](performance.md): findings with thresholds, measurements, and the fixes.
- [Security](security.md): verified controls, findings by severity, the npm audit.
- [User Experience](ux.md): task walkthroughs, findings, ordered steps.
- [UI Design](ui-design.md): screenshots, findings, a proposed list item and composer, build options.
- [Codebase Health](codebase-health.md): structure, duplication, tests, dependencies, the storage-model decision.
- [Developer Experience](developer-experience.md): the measured loop, onboarding, CI and deployment gaps.
- [Feature Ideas](features.md): twenty-two ideas with value and effort, starting with parcels in detail.
- [Web App Plan](web-app.md): architecture, stack comparison, auth, milestones.
- [Roadmap](roadmap.md): phases, lanes and work packages that can run in parallel.
