# Chrome Extension

`src/chrome-extension` is a Manifest V3 extension written in TypeScript and bundled with esbuild. It is a member of the npm workspace at `src/` (see [The npm workspace](#the-npm-workspace)), and its platform-free logic lives in the shared `@passel/core` package.

## What it does

- **Popup:** sign in and out, send the current page, send typed text or a link, and browse the live packet feed. Tiles can be opened, copied or deleted, and a delete can be undone for 4 seconds. All feedback (send and sign-in results, copies, deletes with their undo) goes through one snackbar that overlays the bottom of the popup, so nothing shifts the layout.
- **Context menus:** send a link, the selected text, or an image's URL.

## Modules

| File | Responsibility |
|---|---|
| `background.ts` | The service worker. Handles context menus, routes messages from the popup, schedules deletes so they still happen after the popup closes, and shows badge feedback. |
| `popup.ts` | The popup's entry point; it starts `popup/controller.ts`. Every API call and sign-in request goes to the service worker. |
| `popup/controller.ts` | The popup's session: who is signed in, the live connection to the service worker, sign-in and sign-out, and sending, handing off to the parts below. |
| `popup/feed-view.ts` | The feed as the popup shows it: tiles, the row after the oldest packet, the sync label, paging, copying, and deleting with undo, on top of `FeedStore`. |
| `popup/composer.ts` | Sending the current page and typed text or links, and pasting into the composer. |
| `popup/toast.ts`, `popup/dom.ts` | The one snackbar all feedback goes through; the popup's fixed elements, the message channel to the service worker, and the element and icon builders. |
| `popup.css` | The popup's styles. It imports the design tokens from `@passel/ui`, and esbuild inlines them into `dist/popup.css`. |
| `auth.ts` | Google sign-in through `chrome.identity.launchWebAuthFlow`, using the implicit flow with `response_type=id_token`; in a development build with `DEV_SIGN_IN`, also signing in as a development user through the API's `Dev` scheme. |
| `api.ts` | Authenticated fetches. On a 401 it refreshes the token and retries once. Also the packet endpoints and the server-sent events (SSE) stream. |
| `live.ts` | Keeps one SSE connection open while a popup is open, and reconnects with backoff. |
| `cache.ts` | The feed cache in `chrome.storage.local`. |

From `@passel/core` (`src/packages/core`), shared with the web app to come: `feed.ts` (`FeedStore`: the feed's state, including paging, merging live events and undo), `packets.ts` (parsing the wire format), `sse.ts` (the SSE parser), `authorization.ts` (`Bearer` for a Google ID token, `Dev <user>` for a development user) and `types.ts` (the wire, message and cache types). None of them touches `chrome.*` or the DOM, and their tests run there.

The ID token is kept in `chrome.storage.session`, which is cleared when the browser closes. Only a login hint goes in local storage, so the extension can sign the user back in silently. A development user ([Run without Google](../getting-started/running-locally.md#run-without-google)) is kept in local storage until sign-out, along with the name last used, which the popup offers again.

The feed cache holds only the first page (50 packets) and the cursor after it, so it never grows as the user scrolls through older pages; the in-memory list keeps every page loaded while the popup is open.

## Build

`build.mjs` bundles `src/background.ts`, `src/popup.ts` and `src/popup.css` into `dist/`, following the workspace packages into the bundle, and copies `static/` alongside them. It needs two values and takes a third. Values already in the environment take precedence over a local `.env`:

| Variable | Value |
|---|---|
| `API_URL` | The **packets endpoint** (for example `https://localhost:7030/api/packets`), not the API root. The extension appends `/events` and `/{id}` to it. |
| `GOOGLE_CLIENT_ID` | The extension's OAuth client id |
| `DEV_SIGN_IN` | `true` lets a development build sign in as a development user through the API's Development-only `Dev` scheme, without Google; `GOOGLE_CLIENT_ID` may then be left empty, and the popup offers only the development sign-in. A minified build never has it. The AppHost sets it. |

The manifest's `host_permissions` are generated from the `API_URL` origin, so the extension can only call its own API.

Run `npm install` once in `src/` (the workspace root, not the extension folder), then, in `src/chrome-extension`:

| Command | What it does |
|---|---|
| `npm run dev` / `npm run dev:watch` | Unminified build with source maps (the AppHost runs `dev:watch`) |
| `npm run build` | Type-check, then a minified build |
| `npm run typecheck` | The TypeScript check of the extension |
| `npm run harness` | Serves the built popup with `chrome.*` stubbed; see [The popup harness](#the-popup-harness) |
| `dotnet atom BuildChromeExtension` | Release build, zipped into `atom-publish/ChromeExtension` |

The tests live with the code they test, in `@passel/core`; `npm test` in `src/` runs every package's tests, and `dotnet atom TestChromeExtension` runs the type-checks and tests of the whole workspace.

Load `dist/` unpacked; see [Running Locally](../getting-started/running-locally.md#chrome-extension). The source folder deliberately has no manifest, so it can't be loaded by mistake.

## The popup harness

`harness/` serves the built popup in an ordinary browser tab, with a stand-in for the `chrome.*` APIs (`harness/chrome-stub.js`) and canned packets, so the popup can be looked at and clicked through without loading the extension or running the API. Build first with `npm run dev`, then run `npm run harness` and open `http://127.0.0.1:8765/`. `?state=signedout` shows the signed-out popup, `?state=empty` an empty feed, and `&lastDevUser=alice` offers that development user. The stub behaves like the service worker where the popup can tell: sends echo the packet, deletions wait out the undo window and a cancel stops them, and `passel.created({...})` in the console makes a packet arrive live.

## The npm workspace

`src/package.json` declares an npm workspace with one lockfile, `src/package-lock.json`, for every TypeScript project:

| Member | What it holds |
|---|---|
| `packages/core` (`@passel/core`) | The client logic with no platform APIs, and its tests, including the contract checks against `contracts/` |
| `packages/ui` (`@passel/ui`) | The design tokens as CSS custom properties |
| `chrome-extension` | The extension, consuming both |

The packages are consumed from source: their `exports` point at the TypeScript and CSS files, and each app's bundler compiles them, so nothing is built in a package. The web app will join the workspace as another member (`apps/web`), consuming the same packages.

The `key` in `static/manifest.json` pins the extension id to `aikjdmilnbdacmaeahelifppjjgppemj`. The OAuth redirect URI depends on that id.
