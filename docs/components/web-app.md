# Web App

`src/web` is Passel on the web: a landing page, the privacy and terms pages, and the app itself under `/app/`. It is a member of the npm workspace at `src/` beside the extension (see [The npm workspace](chrome-extension.md#the-npm-workspace)), and its feed, API client and live connection come from `@passel/core`, its colours from `@passel/ui`. This is milestone 1 of the [Web App Plan](../evaluation/web-app.md) (roadmap 1.E2) and the public pages (1.E3).

## What it does

- **Landing page** (`/`): what Passel is, the three steps, and where to get the extension and the app (both "coming soon" until they are listed).
- **Privacy policy and terms** (`/privacy/`, `/terms/`): drafts, pending legal review; see [The privacy and terms drafts](#the-privacy-and-terms-drafts).
- **The app** (`/app/`): sign in with Google, or as a development user in a development build; then the feed, newest at the top, read-only for now. Older items load as the end of the list scrolls into view. While the tab is visible the feed stays live: items sent from other devices appear at once, deletions disappear, and a link's preview (its page's title, description and image) arrives with the `packet-updated` event. Each item shows as a link (with its preview), a note (long ones collapsed behind **Show more**) or a file, which can't be opened here yet. The top bar shows **Live**, or when the feed was last synced, a refresh button, the account and **Sign out**.

Sending, deleting with undo, the offline cache, installing as an app, and the mobile and two-pane layouts are milestone 2 (roadmap 2.E1).

## Stack

The app is [Preact](https://preactjs.com/) components bundled by esbuild, like the extension (`build.mjs`). One bundler serves the whole workspace, and it already compiles the shared packages from source. Preact gives the components and hooks the plan asks for, rather than the extension's hand-written DOM code, for about 4 KB gzipped; the minified app is about 40 KB before compression. The static pages need no framework: they are plain HTML with no script. SvelteKit or Vite would add routing and prerendering, which milestone 1 doesn't need; that choice can be revisited when the app grows routes (`/app/p/{id}`, `/app/settings`).

## Modules

| File | Responsibility |
|---|---|
| `src/app/main.tsx` | The entry point: makes the session, the API client and Google sign-in, hands them to the controller, follows the page's visibility, and renders the views into `#app` |
| `src/app/controller.ts` | `AppController`: who is signed in, the `FeedStore`, the `LiveConnection`, and the rules between them (see [Live updates](#live-updates)). No DOM, so it is unit-tested |
| `src/app/session.ts` | `Session`: the token for this tab, checking a Google credential before accepting it, the development user, and the hint that offers One Tap |
| `src/app/google.ts` | Loads Google Identity Services and wraps its button and One Tap |
| `src/app/labels.ts` | The wording: what to call an item, the sync label, what the end of the feed says, which notes collapse |
| `src/app/views/` | The Preact views: `App`, `SignInPage`, `FeedPage` (with the row after the oldest item), `Tile` and the icons |
| `src/app/app.css`, `src/site.css` | The app's styles and the static pages' styles; both import the tokens from `@passel/ui` |
| `static/` | The static pages, the app's HTML shell and the icons, copied into `dist/` |

From `@passel/core`: `FeedStore`, `PacketsApi` (the packet endpoints over `fetch`, with the `Authorization` header and the problem messages), `LiveConnection` (the event stream, reconnecting with jittered backoff), the packet and event parsers, and the JWT helpers.

## Sign-in

Google sign-in uses [Google Identity Services](https://developers.google.com/identity/gsi/web): the **Sign in with Google** button, and One Tap for someone who signed in on this browser before and didn't sign out. The ID token it returns is issued for the **web** client id, which is the API's main audience, so the API takes it as it is. Before accepting it, the session checks that it was issued by Google for that client id, carries the nonce this tab asked for, and hasn't expired; the API checks the signature.

The token lives in the tab's session storage: a reload keeps it, closing the tab drops it. A Google ID token lasts an hour and can't be renewed without the user, so when it runs out, or the API refuses it, the app returns to the sign-in page saying the session expired and offers One Tap, which signs a returning user straight back in where Google allows it. First-party sessions with refresh tokens (roadmap 1.B1) will remove that hourly step.

A development build with `DEV_SIGN_IN=true` (the AppHost's) also offers a **Development user** field, which signs in through the API's Development-only `Dev` scheme, like the extension's; see [Run without Google](../getting-started/running-locally.md#run-without-google). The name is kept in local storage until sign-out, and the last one used is offered again. A production build never has it. Built without a Google client id, which only a `DEV_SIGN_IN` build allows, the page offers only the development sign-in.

## Live updates

While the tab is signed in and visible, the controller keeps the API's event stream open through `LiveConnection`; hiding the tab closes it. The stream counts as live from the server's first `resync`, and the controller then fetches the first page and merges it on top, so anything missed while the stream was closed shows up. The first resync after the stream opens is fetched at once; resyncs after a reconnect wait a random moment of up to three seconds, so tabs that all lost the stream together (a server restart) don't all fetch at once. A dropped stream reconnects after a jittered backoff from one second up to a minute. A stream refused for lack of a session signs the tab out instead.

`packet-created`, `packet-deleted` and `packet-updated` go to the `FeedStore`, which applies them with the rules the extension and the mobile app share (`contracts/feed-scenarios.json`): a `packet-updated` event replaces a listed item in place and never adds one.

## Build

`build.mjs` bundles `src/app/main.tsx`, `src/app/app.css` and `src/site.css` into `dist/`, following the workspace packages into the bundle, and copies `static/` alongside them. It needs two values and takes two more; values already in the environment take precedence over a local `.env` (template: `.env.example`):

| Variable | Value |
|---|---|
| `API_URL` | The **packets endpoint** (for example `https://localhost:7030/api/packets`), not the API root. The app appends `/events` to it. |
| `GOOGLE_CLIENT_ID` | The Google OAuth **web** client id. Its Authorized JavaScript origins must include the app's origin; see [Google OAuth Clients](../getting-started/google-oauth.md). |
| `DEV_SIGN_IN` | `true` lets a development build sign in as a development user; `GOOGLE_CLIENT_ID` may then be left empty. A minified build never has it. The AppHost sets it. |
| `PORT` | The dev server's port, default 5245. The AppHost sets it. |

The app's HTML gets a Content-Security-Policy from the build: scripts from its own origin and Google Identity Services only, requests to its own origin, the API's origin and Google, preview images from any `https` site, and no plugins, `<base>` or form targets. The static pages allow no script at all.

Run `npm install` once in `src/`, then, in `src/web`:

| Command | What it does |
|---|---|
| `npm run dev` | Unminified build with source maps, rebuilt on every change and served at `http://localhost:<PORT>/`, reloading the page after each rebuild (the AppHost runs it) |
| `npm run build` | Type-check, then a minified build into `dist/` |
| `npm run serve` | A minified build, served the same way, to look at what will ship |
| `npm run typecheck`, `npm test` | The TypeScript check, and the Vitest suite in `test/` |
| `dotnet atom BuildWebApp` | Release build into `atom-publish/WebApp`, with `api-base-url-production` and `server-client-id`; see [Build Targets](../reference/build-targets.md) |

## Hosting

`dist/` is a static site for any static host:

| Path | Serve |
|---|---|
| `/` | `index.html` |
| `/privacy/`, `/terms/` | `privacy/index.html`, `terms/index.html` |
| `/app/` | `app/index.html`, with `app/app.js` and `app/styles.css` |
| Anything else | `404.html`, with status 404 |

File names carry no content hash, so the host should have browsers revalidate (`Cache-Control: no-cache`) rather than cache them for long. A host that can set headers should also send `frame-ancestors 'none'` and HSTS, which a `<meta>` policy can't. The API must list the site's origin in `Cors:AllowedOrigins` ([Configuration](../reference/configuration.md#web-api)), and the Google web client must list it as an Authorized JavaScript origin.

No workflow deploys the site yet. Hooking `BuildWebApp` into the deployment (roadmap 0.A2 chooses the Azure services) is a follow-up; until then, the site exists only as the build output.

## Tests

`npm test` in `src` runs `src/web/test` with the other workspace members:

- `controller.test.ts` drives `AppController` against a fake API: signing in and out, the reload of a signed-in tab, live events, the first and later resyncs, the tab being hidden and shown, an expired session, a refused stream, and failed loads.
- `session.test.ts` covers accepting and refusing Google credentials, expiry, the One Tap hint and development users.
- `views.test.tsx` renders the views to HTML with `preact-render-to-string`: tiles with and without previews, escaping, links that aren't `http(s)`, the feed's end row, and the sign-in page in each build.
- `labels.test.ts` covers the wording; `static-pages.test.ts` checks that every link on the static pages resolves, that they carry no script and use the ADR 0002 vocabulary, and that the privacy and terms drafts say they are drafts and quote the limits the API's options actually default to.

## The privacy and terms drafts

The two pages describe what the code does today, and they are marked as drafts pending legal review. Facts the repository doesn't hold (the operator, contact, hosting region, retention of logs and backups, the legal clauses) are highlighted placeholders such as `[CONTACT EMAIL]`; `static-pages.test.ts` fails if one isn't marked. Before publishing, fill them all in, have the pages reviewed, and remove the draft notice and the `noindex` tag. When the code changes what it stores or does, update the pages; their sources:

| Statement | Where it comes from |
|---|---|
| What is stored with an item, and that a delete erases it | `Domain/Packet.cs`, `PacketStoreService`, [ADR 0001](../decisions/0001-packets-as-documents.md) |
| Only the Google `sub` is kept, as the user id | The endpoints read `ClaimTypes.NameIdentifier`; nothing stores the email or name |
| How a preview is fetched, its limits and its `User-Agent`; the image is never fetched by the server | `LinkPreviewFetcher`, `LinkPreviewOptions`, `LinkPreviewAddressPolicy`, `LinkPreviewResponse.ImageUrl` |
| Rate limits and quotas | `RateLimitOptions`, `QuotaOptions` (checked by the test) |
| Logs, traces and metrics | `Invex.Passel.ServiceDefaults`: OpenTelemetry for ASP.NET Core and `HttpClient`; the log messages carry internal ids, not content |
| What each client keeps | The web app's `session.ts`, the extension's `auth.ts` and `cache.ts`, the mobile app's `auth_repository.dart` and `packet_cache.dart` |
