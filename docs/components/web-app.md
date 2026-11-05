# Web App

`src/web` is Passel on the web: a landing page, the privacy and terms pages, and the app itself under `/app/`. It is a member of the npm workspace at `src/` beside the extension (see [The npm workspace](chrome-extension.md#the-npm-workspace)), and its feed, API client and live connection come from `@passel/core`, its colours from `@passel/ui`. This is milestone 1 of the [Web App Plan](../evaluation/web-app.md) (roadmap 1.E2), apart from its preview deployment (see [Hosting](#hosting)), and the public pages (1.E3).

## What it does

- **Landing page** (`/`): what Passel is, the three steps, and where to get the extension and the app (both "coming soon" until they are listed). Its footer, like the privacy and terms pages', links to the privacy policy, the terms and the documentation.
- **Privacy policy and terms** (`/privacy/`, `/terms/`): drafts, pending legal review; see [The privacy and terms drafts](#the-privacy-and-terms-drafts).
- **The app** (`/app/`): sign in with Google, or as a development user in a development build; then the feed, newest at the top, read-only for now. Older items load as the end of the list scrolls into view. While the tab is visible the feed stays live: items sent from other devices appear at once, deletions disappear, and a link's preview (its page's title, description and image) arrives with the `packet-updated` event. Each item shows as a link (with its preview), a note (long ones collapsed behind **Show more**) or a file, which can't be opened here yet. The top bar shows **Live**, or when the feed was last synced, a refresh button, the account and **Sign out**.

Sending, deleting with undo, the offline cache, installing as an app, and the mobile and two-pane layouts are milestone 2 (roadmap 2.E1).

## Stack

The app is [Preact](https://preactjs.com/) components bundled by esbuild, like the extension (`build.mjs`). One bundler serves the whole workspace, and it already compiles the shared packages from source. Preact gives the components and hooks the plan asks for, rather than the extension's hand-written DOM code, for about 4 KB gzipped; the minified app is about 40 KB before compression. The static pages need no framework: they are plain HTML with no script. SvelteKit or Vite would add routing and prerendering, which milestone 1 doesn't need; that choice can be revisited when the app grows routes (`/app/p/{id}`, `/app/settings`).

## Modules

| File | Responsibility |
|---|---|
| `src/app/main.tsx` | The entry point: loads the configuration, then makes the session, the API client and Google sign-in, hands them to the controller, follows the page's visibility, and renders the views into `#app`; with no valid configuration it shows `ConfigErrorPage` instead and starts nothing |
| `src/app/config.ts` | Fetches `/app/config.json` and checks it: the API URL and the Google client id (see [Configuration at startup](#configuration-at-startup)) |
| `src/app/flags.ts` | The two build flags compiled in by `build.mjs`: `DEV_SIGN_IN` and `LIVE_RELOAD`, both off in a production build |
| `src/app/controller.ts` | `AppController`: who is signed in, the `FeedStore`, the `LiveConnection`, and the rules between them (see [Live updates](#live-updates)). No DOM, so it is unit-tested |
| `src/app/session.ts` | `Session`: the token for this tab, checking a Google credential before accepting it, the development user, and the hint that offers One Tap |
| `src/app/google.ts` | Loads Google Identity Services and wraps its button and One Tap |
| `src/app/labels.ts` | The wording: what to call an item, the sync label, what the end of the feed says, which notes collapse |
| `src/app/views/` | The Preact views: `App`, `SignInPage`, `FeedPage` (with the row after the oldest item), `Tile`, `ConfigErrorPage` and the icons |
| `src/app/app.css`, `src/site.css` | The app's styles and the static pages' styles; both import the tokens from `@passel/ui` |
| `static/` | The static pages, the app's HTML shell and the icons, copied into `dist/` |
| `policy.mjs` | The Content-Security-Policy headers the dev server sends, the same as the web image's `Caddyfile` |

From `@passel/core`: `FeedStore`, `PacketsApi` (the packet endpoints over `fetch`, with the `Authorization` header and the problem messages), `LiveConnection` (the event stream, reconnecting with jittered backoff), the packet and event parsers, and the JWT helpers.

## Sign-in

Google sign-in uses [Google Identity Services](https://developers.google.com/identity/gsi/web): the **Sign in with Google** button, and One Tap for someone who signed in on this browser before and didn't sign out. The ID token it returns is issued for the **web** client id, which is the API's main audience, so the API takes it as it is. Before accepting it, the session checks that it was issued by Google for that client id, carries the nonce this tab asked for, and hasn't expired; the API checks the signature.

The token lives in the tab's session storage: a reload keeps it, closing the tab drops it. A Google ID token lasts an hour and can't be renewed without the user, so when it runs out, or the API refuses it, the app returns to the sign-in page saying the session expired and offers One Tap, which signs a returning user straight back in where Google allows it. First-party sessions with refresh tokens (roadmap 1.B1) will remove that hourly step.

A development build with `DEV_SIGN_IN=true` (the AppHost's) also offers a **Development user** field, which signs in through the API's Development-only `Dev` scheme, like the extension's; see [Run without Google](../getting-started/running-locally.md#run-without-google). The name is kept in the tab's session storage, like a Google token, so each tab can be a different user and signing in or out in one tab leaves the others alone; the last name used is kept in local storage and offered again, in a new tab too. A production build never has it. Built without a Google client id, which only a `DEV_SIGN_IN` build allows, the page offers only the development sign-in.

## Live updates

While the tab is signed in and visible, the controller keeps the API's event stream open through `LiveConnection`. A tab hidden for a minute closes it; one back in view before then carries on with the stream it had. Opening a stream counts against a per-user limit that the extension and the phone share (`RateLimits:StreamsPerMinute`, 10 by default), so a stream per glance at another tab would soon use it up for every device. If nothing has loaded yet, a failed first resync shows as a failed first load, with **Retry**. The stream counts as live from the server's first `resync`, and the controller then fetches the first page and merges it on top, so anything missed while the stream was closed shows up. The first resync after the stream opens is fetched at once; resyncs after a reconnect wait a random moment of up to three seconds, so tabs that all lost the stream together (a server restart) don't all fetch at once. A dropped stream reconnects after a jittered backoff from one second up to a minute. A stream refused for lack of a session signs the tab out instead.

`packet-created`, `packet-deleted` and `packet-updated` go to the `FeedStore`, which applies them with the rules the extension and the mobile app share (`contracts/feed-scenarios.json`): a `packet-updated` event replaces a listed item in place and never adds one.

## Configuration at startup

The built site holds no environment's values, so one build, and one web image, serves every environment. Before anything else the app fetches `/app/config.json` from its own origin, revalidating it every time:

```json
{"API_URL": "https://api.passel.link/api/packets", "GOOGLE_CLIENT_ID": "<id>.apps.googleusercontent.com"}
```

`API_URL` is the **packets endpoint**, not the API root; the app appends `/events` to it. It must be an `https` URL, or `http` to `localhost`, `127.0.0.1` or `[::1]` only, with no credentials, query or fragment. `GOOGLE_CLIENT_ID` is the Google OAuth **web** client id and must end in `.apps.googleusercontent.com`; only a development build with `DEV_SIGN_IN` may leave it empty. A missing file, a file that is not JSON, or a value that breaks a rule stops the app before it makes a session, an API client or loads Google's script: the page says Passel can't start and why, rather than half-working against the wrong API.

A JSON file rather than a script keeps the policy strict: it is data, so nothing in it can run, and the app's `connect-src 'self'` already allows the fetch. The web image writes it when its container starts ([The web image](#the-web-image)); the dev server answers it from `.env` ([Build](#build)).

## Build

`build.mjs` bundles `src/app/main.tsx`, `src/app/app.css` and `src/site.css` into `dist/`, following the workspace packages into the bundle, and copies `static/` alongside them. A production build (`npm run build`) needs no values at all. The dev server (`npm run dev`, `npm run serve`) needs two and takes two more; values already in the environment take precedence over a local `.env` (template: `.env.example`):

| Variable | Value |
|---|---|
| `API_URL` | The **packets endpoint** (for example `https://localhost:7030/api/packets`), not the API root. The dev server serves it in `/app/config.json` and puts its origin into the app's policy. |
| `GOOGLE_CLIENT_ID` | The Google OAuth **web** client id, served in `/app/config.json`. Its Authorized JavaScript origins must include the app's origin; see [Google OAuth Clients](../getting-started/google-oauth.md). |
| `DEV_SIGN_IN` | `true` compiles the development sign-in into a development build (`npm run dev`); `GOOGLE_CLIENT_ID` may then be left empty. A minified build never has it. The AppHost sets it. |
| `PORT` | The dev server's port, default 5245. The AppHost sets it. |
| `WEB_APP_OUT_DIR` | Where to build, instead of `dist/`; read from the environment only. `build.test.ts` uses it. |

The security policies are response headers, not `<meta>` tags, since the app's depends on the API's origin and a header can also forbid framing. Everything under `/app/` gets the app's Content-Security-Policy: scripts from its own origin and Google Identity Services only, requests to its own origin, the API's origin and Google, preview images from any `https` site, and no plugins, `<base>`, form targets or framing. Every other page, the 404 page included, gets one that allows no script at all. The web image's `Caddyfile` sends them in production and `policy.mjs` in the dev server, and `static-pages.test.ts` checks that the two agree. The dev server stands in front of esbuild's, which can't set headers, and also sends `Referrer-Policy: no-referrer-when-downgrade`, which Google's sign-in needs on plain `http://localhost`.

Run `npm install` once in `src/`, then, in `src/web`:

| Command | What it does |
|---|---|
| `npm run dev` | Unminified build with source maps, rebuilt on every change and served at `http://localhost:<PORT>/`, reloading the page after each rebuild (the AppHost runs it) |
| `npm run build` | Type-check, then a minified build into `dist/` |
| `npm run serve` | A minified build, served the same way, to look at what will ship. It signs in with Google only, never as a development user; like `npm run dev` it serves `/app/config.json` and the policies from `.env`, and the referrer policy Google sign-in needs on `http://localhost` |
| `npm run typecheck`, `npm test` | The TypeScript check, and the Vitest suite in `test/` |
| `dotnet atom BuildWebApp` | Release build into `atom-publish/WebApp`, the same for every environment; it takes no parameters. See [Build Targets](../reference/build-targets.md) |

## Hosting

`dist/` is a static site, but not for a host that only serves files: the host must also serve the app's configuration and send the security policies as headers. The web image does all of it, and its `deploy/web/Caddyfile` is the reference for any other host:

| Path | Serve |
|---|---|
| `/` | `index.html` |
| `/privacy/`, `/terms/` | `privacy/index.html`, `terms/index.html` |
| `/app/` | `app/index.html`, with `app/app.js` and `app/styles.css` |
| `/app/config.json` | The environment's configuration ([Configuration at startup](#configuration-at-startup)), revalidated on every load |
| `/privacy`, `/terms`, `/app` | A redirect to the same path with the trailing slash. The mobile app links to `https://passel.link/privacy` and `https://passel.link/terms` in this form |
| Anything else | `404.html`, with status 404 |

File names carry no content hash, so the host should have browsers revalidate (`Cache-Control: no-cache`) rather than cache them for long. It sends the two Content-Security-Policy headers ([Build](#build)) and HSTS. The API must list the site's origin in `Cors:AllowedOrigins` ([Configuration](../reference/configuration.md#web-api)), and the Google web client must list it as an Authorized JavaScript origin.

### The web image

The self-hosted deployment ([ADR 0004](../decisions/0004-hosting-on-docker-compose.md)) serves the site from the `passel-web` image: Caddy, from `deploy/web/Dockerfile`, `deploy/web/Caddyfile` and `deploy/web/entrypoint.sh`, with the site from `BuildWebApp` in `/srv`. Every build of `main` builds it after the site and pushes it to `ghcr.io/invex-games/passel-web` (`PublishImages`), tagged with the version and `sha-` and the commit. The image holds no environment's values, so the same image runs in every environment and is promoted unchanged from one to the next. It is configured when its container starts, from two environment variables:

| Variable | Value |
|---|---|
| `API_ORIGIN` | The API's origin, such as `https://api.passel.link`: `https`, a host name and an optional port, nothing else (`http` only for `localhost` or `127.0.0.1`). One trailing slash is dropped |
| `GOOGLE_CLIENT_ID` | The Google OAuth web client id, `<id>.apps.googleusercontent.com` |

Compose sets them from the server's `.env`: `API_ORIGIN` is `https://<API_HOST>` and `GOOGLE_CLIENT_ID` is `GOOGLE_WEB_CLIENT_ID`, the same client id the API accepts ([Configuration](../reference/configuration.md#compose-deployment)). The entrypoint checks both and writes `/run/passel-web/config.json`, which Caddy serves as `/app/config.json`, then starts Caddy with the checked origin, which the `Caddyfile` puts into the app's `connect-src` (`{$API_ORIGIN}`). A value that is missing or breaks a rule stops the container with a message saying which and why (exit code 64), so a misconfigured server never serves a half-working app. The checks admit no quote, backslash, space or control character, which keeps the values from breaking out of the JSON or, since Caddy substitutes an environment value into its configuration as text, out of the header.

The image keeps the rules above:

- It listens on plain HTTP on port 8080, as user 1000, with Caddy's admin endpoint, automatic HTTPS and saved configuration off. TLS ends at the proxy in front of it. The only directory it writes to is `/run/passel-web`, which the image gives to user 1000, so the entrypoint needs no capability; a container with a read-only root filesystem needs a tmpfs there (`--tmpfs /run/passel-web:uid=1000,gid=1000`).
- `/privacy`, `/terms` and `/app` answer 308 with the trailing slash; anything that is not a file answers 404 with `404.html`.
- Every response, the redirects and the 404 page included, carries `Cache-Control: no-cache`, `Strict-Transport-Security: max-age=31536000; includeSubDomains`, a `Content-Security-Policy` with `frame-ancestors 'none'` (the app's under `/app/`, the no-script one elsewhere, a 404 under `/app/` included), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`, and no `Server` header. Caddy's `ETag` and `Last-Modified` let browsers revalidate with a 304, `/app/config.json` included.
- Responses are compressed with zstd or gzip, and request bodies are capped at 1 MB.
- The Caddy binary has its capability to bind low ports removed, so the container runs with every capability dropped.

To build and look at it locally (nothing is pushed off `main`), run `dotnet atom SetupBuildInfo` (with `--skip`, `PublishImages` uses the version that last stored), then `dotnet atom BuildWebApp`, then `dotnet atom PublishImages --skip`, and `docker run --rm -p 127.0.0.1:18081:8080 --cap-drop ALL -e API_ORIGIN=https://api.example.test -e GOOGLE_CLIENT_ID=<id>.apps.googleusercontent.com invex-games/passel-web:<version>`. Locally the images are named without the registry, as `invex-games/passel-webapi` and `invex-games/passel-web`.

The compose stack runs the image as `web`, behind the proxy, and the `DeployCompose` workflow deploys it with the API ([Releasing and Deploying](../guides/deploying.md#self-hosted-docker-compose)); the preview deployment in roadmap 1.E2 is still to come. The production build is checked on every pull request: `build.test.ts` runs `build.mjs` as part of `npm test`. Before the site goes up:

- `docs.passel.link`, which the static pages' footers link to as **Documentation**, must serve the DocFX site (`PublishDocs` pushes it to GitHub Pages).
- The mobile app's **Privacy policy** and **Terms of service** tiles stay disabled until the pages are hosted; turn on `legalPagesPublished` in `settings_page.dart` then.

## Tests

`npm test` in `src` runs `src/web/test` with the other workspace members:

- `controller.test.ts` drives `AppController` against a fake API: signing in and out, the reload of a signed-in tab, live events, the first and later resyncs, the tab being hidden briefly and for longer, an expired session, a refused stream, and failed loads, including a first resync that overtakes the first refresh and fails.
- `session.test.ts` covers accepting and refusing Google credentials, expiry, the One Tap hint and development users, one per tab.
- `views.test.tsx` renders the views to HTML with `preact-render-to-string`: tiles with and without previews, escaping, links that aren't `http(s)`, the feed's end row, the sign-in page in each build, and the page shown when the configuration is not valid.
- `config.test.ts` covers loading `/app/config.json` and every rule it is checked against: `https` only, `http` to this machine only, no credentials, query or fragment, a real client id unless the development sign-in is built in, and a missing, unreadable or non-JSON file.
- `build.test.ts` runs the production build into a temporary folder with no values set and checks its output: every page and bundle, no source maps, no configuration, no policy in the HTML and no API address or client id compiled in. It also starts the dev server on a free port and checks `/app/config.json` and the two policies it sends.
- `labels.test.ts` covers the wording; `static-pages.test.ts` checks that every link on the static pages resolves, that they carry no script and no policy of their own and use the ADR 0002 vocabulary and link to the documentation, that the web image's `Caddyfile` sends the same two policies as the dev server, and that the privacy and terms drafts say they are drafts, quote the limits the API's options actually default to, and don't promise a single preview request.

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
