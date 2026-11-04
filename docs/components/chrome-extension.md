# Chrome Extension

`src/chrome-extension` is a Manifest V3 extension written in TypeScript and bundled with esbuild. It is a member of the npm workspace at `src/` (see [The npm workspace](#the-npm-workspace)), and its platform-free logic lives in the shared `@passel/core` package.

## What it does

- **Popup:** sign in and out, send the current page, send typed text or a link, and browse the live packet feed. Tiles can be opened, copied or deleted, and a delete can be undone for 4 seconds. All feedback (send and sign-in results, copies, deletes with their undo) goes through one snackbar that overlays the bottom of the popup, so nothing shifts the layout.
- **Side panel:** the popup's page, laid out for a tall, narrow panel that stays open beside the page while you move between tabs and open links. Open it with the panel button in the popup's header, the **Open Passel in the side panel** shortcut, or Chrome's own side panel menu. The composer stays at the top while the feed scrolls under it, and notes show twice as many lines before "Show more". The popup stays the quick way to send.
- **Keyboard shortcuts:** open the popup, send the page you're on (or the text selected on it), and open the side panel. See [Keyboard shortcuts](#keyboard-shortcuts).
- **Address bar:** type `passel`, a space and some text, then press Enter to send it: a link if it's a web address, otherwise a note. See [The passel keyword](#the-passel-keyword).
- **Context menus:** send a link, the selected text, or an image's URL.

Sends from a shortcut, the address bar or a context menu show their outcome on the toolbar icon: a green tick, or a red `!` when nothing could be sent.

## Modules

| File | Responsibility |
|---|---|
| `background.ts` | The service worker. Handles context menus, routes messages from the popup, schedules deletes so they still happen after the popup closes, shows badge feedback, and tells every open page when someone signs in or out. |
| `commands.ts` | The keyboard commands the service worker handles: sending the page or its selection, and opening the side panel. |
| `omnibox.ts` | The `passel` address bar keyword: the suggestion that says what Enter will send, and sending it. |
| `popup.ts` | The entry point of the popup and the side panel, which are the same page; it starts `popup/controller.ts`. Every API call and sign-in request goes to the service worker. |
| `popup/controller.ts` | The popup's session: who is signed in, the live connection to the service worker, sign-in and sign-out, sending, and moving into the side panel, handing off to the parts below. |
| `popup/feed-view.ts` | The feed as the popup shows it: tiles, the row after the oldest packet, the sync label, paging, copying, and deleting with undo, on top of `FeedStore`. |
| `popup/composer.ts` | Sending the current page and typed text or links, and pasting into the composer. |
| `popup/toast.ts`, `popup/dom.ts` | The one snackbar all feedback goes through; the popup's fixed elements, whether the page is the side panel, the message channel to the service worker, and the element and icon builders. |
| `popup.css` | The styles of the popup and, under `body.side-panel`, of the side panel. It imports the design tokens from `@passel/ui`, and esbuild inlines them into `dist/popup.css`. |
| `auth.ts` | Google sign-in through `chrome.identity.launchWebAuthFlow`, using the implicit flow with `response_type=id_token`; in a development build with `DEV_SIGN_IN`, also signing in as a development user through the API's `Dev` scheme. |
| `api.ts` | Authenticated fetches. On a 401 it refreshes the token and retries once. Also the packet endpoints and the server-sent events (SSE) stream. |
| `live.ts` | Keeps one SSE connection open while a popup or the side panel is open, reconnects with backoff, and opens it again with the new session after a sign-in or sign-out. |
| `cache.ts` | The feed cache in `chrome.storage.local`. |

From `@passel/core` (`src/packages/core`), shared with the [web app](web-app.md): `feed.ts` (`FeedStore`: the feed's state, including paging, merging live events and undo), `packets.ts` (parsing the wire format), `sse.ts` (the SSE parser), `authorization.ts` (`Bearer` for a Google ID token, `Dev <user>` for a development user) and `types.ts` (the wire, message and cache types). None of them touches `chrome.*` or the DOM, and their tests run there.

The ID token is kept in `chrome.storage.session`, which is cleared when the browser closes. Only a login hint goes in local storage, so the extension can sign the user back in silently. A development user ([Run without Google](../getting-started/running-locally.md#run-without-google)) is kept in local storage until sign-out, along with the name last used, which the popup offers again.

The feed cache holds only the first page (50 packets) and the cursor after it, so it never grows as the user scrolls through older pages; the in-memory list keeps every page loaded while the popup is open.

## The side panel

`build.mjs` writes `dist/sidepanel.html` from `static/popup.html`, with a `side-panel` class on the body, so the side panel runs the popup's own script and markup and the two can't drift apart. The class is all that differs: `popup.css` sizes the page to the panel and keeps the composer in view, the popup's button for opening the side panel is hidden, and the composer asks for the `tabs` permission before sending the page (see [Permissions](#permissions)). The side panel holds a live connection to the service worker like the popup, so the feed stays live for as long as the panel is open.

Because the panel stays open, a sign-in or sign-out can happen elsewhere while it shows a feed: in a popup opened beside it, or through a shortcut, the `passel` keyword or a context menu that needed a sign-in. `auth.ts` reports every sign-out, and every sign-in that the user saw or that brought another account (a silent refresh of the same account is not reported). The service worker then starts the event stream again with the new session, since the stream it had open was opened with the previous token and the API would keep it open until that token expires. It also sends a `sessionChanged` message to every open page, signed in or not. Each page asks for the auth state again. If the account it shows has changed, it forgets its feed, shows the new session and fetches that account's feed, so one account's packets are never shown or cached under another.

## Keyboard shortcuts

| Command | Suggested key | Mac | What it does |
|---|---|---|---|
| Open the popup (`_execute_action`) | Alt+Shift+P | Control+Shift+P | Opens the popup, as the toolbar button does |
| Send this page, or the selected text (`send-current-page`) | Alt+Shift+S (Alt+Shift+Y on ChromeOS) | Control+Shift+S | Sends the text selected on the page as a note if there is any, otherwise the page as a link, without opening anything |
| Open Passel in the side panel (`open-side-panel`) | Alt+Shift+O | Control+Shift+O | Opens the side panel in the current window |

The keys avoid Chrome's own shortcuts: Chrome uses Alt+Shift with T, I, A and N on Windows and Linux, ChromeOS uses Alt+Shift+S for its quick settings, and on a Mac the suggestions use the Control key, which Chrome's own shortcuts leave alone. Chrome only assigns a suggested key when the extension is first installed and the key is free, so one can be missing; anyone can change them at `chrome://extensions/shortcuts`. A command that reads the page works on the tab it was pressed on, through the `activeTab` grant the shortcut gives.

## The passel keyword

The manifest's `omnibox` keyword is `passel`. Typing `passel` and a space in the address bar hands the rest of the line to the extension. Its suggestion says what Enter will send ("Send link: …" or "Send note: …"), or how to use it while nothing is typed yet, and Enter sends it through the service worker: a link if it's an `http` or `https` address and a note otherwise, the same rule as the popup's text field. An empty line sends nothing and shows the red `!`.

## Permissions

| Permission | Why |
|---|---|
| `activeTab` | Reading the address and title of the page the user asked to send, from the popup, a shortcut or a context menu. Granted only for that tab, only by that action. |
| `contextMenus` | The three send entries in the page's context menu |
| `identity` | Google sign-in through `chrome.identity.launchWebAuthFlow` |
| `storage` | The ID token (session storage), the login hint, the feed cache and whether the `tabs` permission was declined (local storage) |
| `sidePanel` | The side panel. No install warning. |
| `scripting` | Reading the text selected on the page for the send shortcut, by running one function in the tab. It only works where `activeTab` has been granted, so it reaches no page the user didn't act on. No install warning. |
| `tabs`, optional | Asked for by the side panel the first time it sends the page: the panel stays open across tabs, and the `activeTab` grant doesn't follow it to the next one. Chrome shows its "Read your browsing history" prompt then, never at install. Chrome doesn't remember a refusal, so the panel does: once the prompt is declined, the panel doesn't ask by itself again, even in a panel opened later. It can still send a page Passel was opened from. For other pages it says why it can't send them and offers **Allow**, which shows the prompt again and sends the page once the permission is granted. |

The only host permission is the API's origin (see [Build](#build)). The side panel and `sidePanel.open` need Chrome 116, so the manifest's `minimum_chrome_version` is 116.

## Build

`build.mjs` bundles `src/background.ts`, `src/popup.ts` and `src/popup.css` into `dist/`, following the workspace packages into the bundle, copies `static/` alongside them, and writes `sidepanel.html` from `popup.html`. It needs two values and takes a third. Values already in the environment take precedence over a local `.env`:

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

The tests of the platform-free logic live with it in `@passel/core`; the extension's own modules, the ones that call `chrome.*`, are tested in `test/` against a stubbed `chrome` (see [Testing](../guides/testing.md)). `npm test` in `src/` runs every package's tests, `npm run test:coverage` adds the coverage reports, and `dotnet atom TestChromeExtension` runs the type-checks and the tests of the whole workspace with coverage.

Load `dist/` unpacked; see [Running Locally](../getting-started/running-locally.md#chrome-extension). The source folder deliberately has no manifest, so it can't be loaded by mistake.

## The popup harness

`harness/` serves the built popup in an ordinary browser tab, with a stand-in for the `chrome.*` APIs (`harness/chrome-stub.js`) and canned packets, so the popup can be looked at and clicked through without loading the extension or running the API. Build first with `npm run dev`, then run `npm run harness` and open `http://127.0.0.1:8765/` for the popup or `http://127.0.0.1:8765/sidepanel.html` for the side panel, framed at Chrome's default panel width and the height of the tab. On either page, `?state=signedout` shows the signed-out popup, `?state=empty` an empty feed, and `&lastDevUser=alice` offers that development user. The stub behaves like the service worker where the popup can tell: sends echo the packet, deletions wait out the undo window and a cancel stops them, and `passel.created({...})` in the console makes a packet arrive live.

## The npm workspace

`src/package.json` declares an npm workspace with one lockfile, `src/package-lock.json`, for every TypeScript project:

| Member | What it holds |
|---|---|
| `packages/core` (`@passel/core`) | The client logic with no platform APIs, and its tests, including the contract checks against `contracts/` |
| `packages/ui` (`@passel/ui`) | The design tokens as CSS custom properties |
| `chrome-extension` | The extension, consuming both |
| `web` | The [web app](web-app.md), consuming both |

The packages are consumed from source: their `exports` point at the TypeScript and CSS files, and each app's bundler compiles them, so nothing is built in a package. `@passel/core` also holds a `fetch`-based API client (`api.ts`), a reconnecting event stream (`live.ts`) and JWT helpers (`jwt.ts`), which the web app uses; the extension still has its own in its service worker, and can move onto them later.

The `key` in `static/manifest.json` pins the extension id to `aikjdmilnbdacmaeahelifppjjgppemj`. The OAuth redirect URI depends on that id.
