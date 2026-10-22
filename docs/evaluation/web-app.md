# Web App Plan

Goal: a first-class client at `passel.link` that matches the mobile app rather than the extension popup: the full feed, sending, live updates, undo, offline cache and settings, in a layout that works on a phone's browser and scales to a desktop screen. Plus a public landing page, since `passel.link` is also where people will look first.

## What "a closer match to the mobile app" means

The popup is a 380 by 600 pixel window that closes when you look away; the mobile app is a full screen with a collapsing app bar, a feed, a floating send button that opens a sheet, swipe to delete with undo, pull to refresh and a sign-in sheet when the session lapses. The web app should reproduce that screen one to one below 600 px wide, and above 840 px widen into a two-pane layout: navigation rail, feed list, detail pane. Everything the mobile app can do, the web app can do; everything a browser can do better (keyboard, drag and drop, a wide screen) it adds.

## Information architecture

| URL | Page |
|---|---|
| `passel.link` | Landing: what it is, get the extension, get the app, sign in |
| `passel.link/app` | The feed with the composer (signed in), or the sign-in page |
| `passel.link/app/p/{id}` | Item detail: full text, link preview, parcel viewer, copy, share, delete |
| `passel.link/app/settings` | Account, devices, theme, retention, storage, export, delete account |
| `passel.link/privacy`, `/terms` | Required for the Chrome Web Store and Google Play listings |
| `api.passel.link` | The existing API |
| `docs.passel.link` | The DocFX site |

Serving the app under `/app` on the same host as the landing page keeps one origin for cookies and share-target registration; `app.passel.link` is the alternative if the landing page is hosted elsewhere.

## Stack options

| | TypeScript PWA (shared core with the extension) | Flutter Web (the mobile app compiled for the browser) | Blazor WebAssembly |
|---|---|---|---|
| Fidelity to the mobile app | Rebuilt from the same design; close, not identical | Identical widgets and behaviour | Rebuilt; MD3 fidelity weakest |
| Code sharing | `feed.ts`, `packets.ts`, `sse.ts`, `types.ts` from the extension are already free of `chrome.*` and move as-is; the extension becomes another consumer | All of `lib/` except sign-in and SSE transport | DTOs with the API via a contracts project; nothing with the clients |
| Initial download | About 100 to 200 KB | 1.5 to 3 MB (CanvasKit or WASM renderer), slow first paint | 2 MB plus with trimming |
| Web platform | Native: text selection, find in page, SEO for the landing page, accessibility, Web Share Target, Web Push, Background Sync, installable PWA | Weak: canvas rendering, no SEO, selection and accessibility via workarounds, share target and push need JS interop | Partial: needs JS interop for Google sign-in, push and share target |
| Google sign-in | Google Identity Services button and One Tap; ID token has the web client id as audience, which the API already accepts | `google_sign_in_web` renders the GIS button; `authenticate()` is unsupported on web so the sign-in UI changes anyway | GIS through JS interop |
| Live updates | `fetch` plus the existing SSE parser | The default `http` client on web is XHR-based and doesn't stream; needs `fetch_client` or similar | `HttpClient` streaming works in WASM |
| Team fit | TypeScript already in the repo | Dart already in the repo | C# already in the repo |
| Verdict | **Recommended** | Good for a quick internal preview; not the public site | Only if the front end must be C# |

### Within the TypeScript option

- **Framework:** SvelteKit is the lighter fit (small bundles, built-in routing, static adapter for CDN hosting, prerendering for the landing page). React with Vite is the safer pick if contributors know React. Both consume the same plain-TypeScript core. Avoid rebuilding the extension's hand-written DOM code for a multi-page app; it was right for a popup, it won't scale to routes, lists and sheets.
- **Monorepo:** an npm workspace at `src/` with `packages/core` (feed store, packet parsing, SSE, API client, auth helpers, tests), `packages/ui` (the MD3 tokens and the handful of components from [UI Design](ui-design.md)), `apps/extension` (the current extension, consuming both packages) and `apps/web`. The AppHost already runs the extension's watch build with `AddJavaScriptApp`; it runs the web app the same way.
- **State and cache:** the `FeedStore` from `feed.ts` with an IndexedDB cache (`idb-keyval`) instead of `chrome.storage`, so the feed shows instantly and offline.
- **PWA:** `vite-plugin-pwa` or Workbox for the app shell, a web manifest with `share_target` (so an installed PWA appears in Android's share sheet, which covers phones without the store app), `display: standalone`, icons from the brand kit, Web Push subscription stored in the device registry, Background Sync for queued sends.
- **Layout:** below 600 px the mobile screen (top bar, feed, FAB and bottom sheet composer, swipe to delete via pointer gestures or an explicit delete); 600 to 840 px a single column with a docked composer; above 840 px a navigation rail, the feed list and a detail pane, keyboard shortcuts (`n` new note, `/` search, `j`/`k` move, `Enter` open, `Delete` delete with undo).
- **Hosting:** static files on a CDN (Azure Static Web Apps or Cloudflare Pages) with the API on its own host; CORS on the API limited to `https://passel.link`. Serving the static files from the API itself is the fallback if a second host is unwanted.

## Authentication on the web

The simplest path that works today: Google Identity Services issues an ID token for the web client id, which is already the API's main audience, so the web app can call the API with no API changes. The catch is the one-hour lifetime: a browser tab can't silently mint a new ID token without a One Tap prompt, so users would see a prompt roughly hourly. That is the strongest reason for first-party sessions ([Features](features.md), item 7): exchange the Google token once for a Passel access token and a rotating refresh token, keep the access token in memory and the refresh token in an `HttpOnly` cookie scoped to `api.passel.link/auth`, and the web session lasts for weeks. Build the web app against Google tokens first so it ships, and switch when sessions land; the API should accept both during the overlap.

## Milestones

| Milestone | Scope | Size |
|---|---|---|
| **M1 Skeleton** | Workspace set up; `packages/core` extracted from the extension with its tests; web app with Google sign-in, read-only feed, live updates, deployed to a preview URL; the AppHost runs it locally | M |
| **M2 Parity** | Send text and links, delete with undo, offline cache, PWA install, the mobile layout below 600 px and the two-pane layout above 840 px, settings page, timestamps and day headers (needs the API change), the agreed vocabulary and ordering | L |
| **M3 Beyond** | Parcels with drag, drop, paste and a viewer; share target; Web Push; search; device targeting; export and account deletion | L to XL, tracks the API features |
| **Landing page** | Prerendered pages: hero, three steps, extension and app buttons, privacy, terms, docs link | S |

M1 and the landing page can start now in parallel with the API work; M2 needs the timestamp change and the vocabulary decision; M3 follows the [Roadmap](roadmap.md) feature tracks.

## Risks

- **Two feed implementations become three.** The feed state machine already exists in TypeScript and Dart. Extracting the TypeScript one into `packages/core` is the fix, not a third copy.
- **Hourly sign-in prompts** until first-party sessions exist. Acceptable for a preview, not for launch.
- **Chrome Web Store and Play Store policies** need the privacy page and account deletion before public listing; the landing page should carry them from the start.
- **Flutter Web temptation.** It is a few commands away from "working" and will look right in a demo; its first-load cost and web-platform gaps make it the wrong public site. Use it for a stakeholder preview only if that's useful.
