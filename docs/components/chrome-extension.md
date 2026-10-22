# Chrome Extension

`src/chrome-extension` is a Manifest V3 extension written in TypeScript and bundled with esbuild.

## What it does

- **Popup:** sign in and out, send the current page, send typed text or a link, and browse the live packet feed. Tiles can be opened, copied or deleted, and a delete can be undone for 4 seconds. All feedback (send and sign-in results, copies, deletes with their undo) goes through one snackbar that overlays the bottom of the popup, so nothing shifts the layout.
- **Context menus:** send a link, the selected text, or an image's URL.

## Modules

| File | Responsibility |
|---|---|
| `background.ts` | The service worker. Handles context menus, routes messages from the popup, schedules deletes so they still happen after the popup closes, and shows badge feedback. |
| `popup.ts` | The popup UI. It sends every API call and sign-in request to the service worker. |
| `auth.ts` | Google sign-in through `chrome.identity.launchWebAuthFlow`, using the implicit flow with `response_type=id_token`; in a development build with `DEV_SIGN_IN`, also signing in as a development user through the API's `Dev` scheme. |
| `authorization.ts` | How a token becomes the `Authorization` header: `Bearer` for a Google ID token, `Dev <user>` for a development user. No `chrome.*` calls, so it is unit-tested. |
| `api.ts` | Authenticated fetches. On a 401 it refreshes the token and retries once. Also the packet endpoints and the server-sent events (SSE) stream. |
| `live.ts` | Keeps one SSE connection open while a popup is open, and reconnects with backoff. |
| `feed.ts` | `FeedStore`: the feed's state, including paging, merging live events and undo. It has no `chrome.*` dependencies, so it can be unit tested. |
| `packets.ts`, `sse.ts`, `cache.ts` | Parsing, the SSE parser, and the feed cache in `chrome.storage.local`. |

The ID token is kept in `chrome.storage.session`, which is cleared when the browser closes. Only a login hint goes in local storage, so the extension can sign the user back in silently. A development user ([Run without Google](../getting-started/running-locally.md#run-without-google)) is kept in local storage until sign-out, along with the name last used, which the popup offers again.

The feed cache holds only the first page (50 packets) and the cursor after it, so it never grows as the user scrolls through older pages; the in-memory list keeps every page loaded while the popup is open.

## Build

`build.mjs` bundles `src/background.ts` and `src/popup.ts` into `dist/` and copies `static/` alongside them. It needs two values and takes a third. Values already in the environment take precedence over a local `.env`:

| Variable | Value |
|---|---|
| `API_URL` | The **packets endpoint** (for example `https://localhost:7030/api/packets`), not the API root. The extension appends `/events` and `/{id}` to it. |
| `GOOGLE_CLIENT_ID` | The extension's OAuth client id |
| `DEV_SIGN_IN` | `true` lets a development build sign in as a development user through the API's Development-only `Dev` scheme, without Google; `GOOGLE_CLIENT_ID` may then be left empty, and the popup offers only the development sign-in. A minified build never has it. The AppHost sets it. |

The manifest's `host_permissions` are generated from the `API_URL` origin, so the extension can only call its own API.

| Command | What it does |
|---|---|
| `npm run dev` / `npm run dev:watch` | Unminified build with source maps (the AppHost runs `dev:watch`) |
| `npm run build` | Type-check, test, then a minified build |
| `npm test` | Vitest |
| `dotnet atom BuildChromeExtension` | Release build, zipped into `atom-publish/ChromeExtension` |

Load `dist/` unpacked; see [Running Locally](../getting-started/running-locally.md#chrome-extension). The source folder deliberately has no manifest, so it can't be loaded by mistake.

The `key` in `static/manifest.json` pins the extension id to `aikjdmilnbdacmaeahelifppjjgppemj`. The OAuth redirect URI depends on that id.
