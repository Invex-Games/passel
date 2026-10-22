# Feature Ideas

A brainstorm of what Passel could do next, starting with the one the codebase is already half-built for: parcels. Each idea has a rough value and effort so the [Roadmap](roadmap.md) can sequence them. Sizes: S under a day, M a few days, L a week or more, XL several weeks.

## Summary

| # | Feature | Value | Effort | Touches | Depends on |
|---|---|---|---|---|---|
| 1 | Parcels: files and images | High | XL | API, storage, all clients | Storage choice |
| 2 | Device registry and push notifications | High | L | API, all clients | Nothing (registry first) |
| 3 | Android share target and process-text | High | S to M | Mobile | Nothing |
| 4 | Timestamps, day headers, relative time | High | S | API, all clients | Nothing |
| 5 | Link previews (title, description, image, favicon) | High | M | API, all clients | SSRF safeguards |
| 6 | Search and filters | Medium | M | API, all clients | Nothing |
| 7 | First-party sessions (token exchange, refresh tokens) | High for web | L | API, all clients | Nothing |
| 8 | Send to a specific device | Medium | M | API, all clients | 2 |
| 9 | Pin, archive, labels | Medium | M | API, all clients | Nothing |
| 10 | Retention and auto-expiry | Medium | S to M | API, settings UI | Nothing |
| 11 | Trash and restore | Medium | S | API, clients | Nothing (event-sourced already) |
| 12 | Extension: side panel, commands, omnibox, send all tabs, drag and drop | High | M | Extension | Nothing (parcels for files) |
| 13 | Mobile: widget, quick-settings tile, app shortcuts, deep links | Medium | M | Mobile | 7 for deep links to work signed-in |
| 14 | Account: delete, export, usage | High for store listing | M | API, mobile, web | Nothing |
| 15 | Public API, API keys, CLI, webhooks | Medium | L | API, new CLI | 7 |
| 16 | Offline send queue | Medium | M | Clients | Nothing |
| 17 | Read and opened state | Low | M | API, clients | Nothing |
| 18 | Collections (send several items as one) | Low | L | All | 1 |
| 19 | Markdown and code notes | Low | M | Clients | Nothing |
| 20 | Desktop app (global hotkey, clipboard) | Medium | XL | New client | 7 |
| 21 | iOS app and share extension | High for reach | XL | Mobile | Apple sign-in, 7 |
| 22 | End-to-end encryption (opt-in) | Low now | XL | All | Design decisions |

## 1. Parcels: bring back files and images

### Where it stands

The first commit had the whole feature on the client side: `PARCELS_URL` in the extension build, `uploadParcel` in `api.ts`, a context-menu image upload in `background.ts`, `ParcelService` and `parcel_cache_manager.dart` (on `flutter_cache_manager`) in the app, an image `FileTile` with tap-to-fullscreen, and image copy and share sheets. It was removed in the hardening commits (`31c9b70`, `f0ec238`, `20e13a1`) because the server had nowhere to put the bytes. The domain model kept the shape: `FilePacketCreated` carries `ParcelId`, `ContentType`, `IsImage` and `FileName`, `PacketAggregate` projects it, and `GET /api/packets` already returns `File` items with `parcelId` and `isImage`. The clients still parse them and render a placeholder. So what's missing is storage, two endpoints, the `File` branch of `POST /api/packets`, and the client features coming back.

### Storage

| Option | Pros | Cons |
|---|---|---|
| **Azure Blob Storage** | The build already uses Azure (Key Vault `invex-passel`); SAS URLs for direct downloads later; lifecycle rules; Aspire has a first-party integration with Azurite for local runs | Ties hosting to Azure |
| **S3-compatible (Cloudflare R2, Backblaze B2, MinIO)** | R2 has no egress fees; portable API; MinIO container locally | Another vendor; the Aspire integration comes from the community toolkit |
| **PostgreSQL (`bytea`)** | No new infrastructure; transactional with the packet; trivial local dev | Database and backup bloat; poor for files over a few MB; the API always proxies bytes |
| **Local disk** | Trivial | Not durable, single instance only |

Recommendation: put an `IParcelStore` interface in front of it from day one (put, get stream, delete, maybe signed URL) and implement **Azure Blob** first, with the Aspire Azure Storage integration running Azurite locally. If hosting ends up elsewhere, an S3 implementation is a day's work behind the same interface. Keep Postgres as a fallback for tests only.

### API

- `POST /api/parcels`: multipart or raw body with `Content-Type`, file name in a header or form field. Limits: 25 MB by default (configurable), per-user quota (for example 500 MB total, enforced with a `413` and surfaced in settings), a rate limit. Images are sniffed and decoded, dimensions recorded, and a thumbnail generated at upload (ImageSharp or SkiaSharp, 512 px WebP) which also strips metadata. Returns `{ parcelId, contentType, size, isImage, width, height }`.
- `POST /api/packets` with `$type: File` and `parcelId`: the parcel must belong to the caller and not already be attached. Parcels not attached within an hour are swept.
- `GET /api/parcels/{id}` and `GET /api/parcels/{id}/thumbnail`: owner check, streamed, `Range` support, `ETag`, `Cache-Control: private, max-age=31536000, immutable` (parcels never change). Non-image, non-PDF types are served with `Content-Disposition: attachment` and the whole endpoint sends `Content-Security-Policy: sandbox` so an uploaded HTML file can never run against the API origin.
- Deletion: when `PacketDeleted` is applied, the parcel is marked for deletion and a background sweep removes blobs and thumbnails (a Marten async projection or a small hosted service; a nightly orphan sweep catches the rest).
- Model: parcels are plain Marten documents (`Parcel { Id, UserId, ContentType, Size, FileName, IsImage, Width, Height, StorageKey, Sha256, CreatedAt, AttachedPacketId }`), not event streams. Only packets need history.
- Later: pre-signed direct-to-storage uploads so the API doesn't proxy bytes. Start with proxying: it enforces limits in one place and keeps a single origin.

### Extension

- Paste an image into the popup or side panel (`ClipboardEvent.clipboardData.files`), show a preview, upload, send.
- Drag and drop files onto the panel.
- "Send image" from the context menu needs the image bytes. The manifest deliberately limits `host_permissions` to the API origin, so the service worker can't fetch arbitrary image URLs. Options: `optional_host_permissions: ["<all_urls>"]` requested on first use (the context-menu click is a user gesture, so `chrome.permissions.request` works there), or let the server fetch the URL (works for the web app too, but is a server-side request forgery risk that needs scheme and private-address blocking, size and time limits). Do the first; add the second only with those safeguards.
- Display: thumbnails in tiles, a viewer, download, copy image to clipboard (PNG only in Chrome), open in a tab.

### Mobile

- Share target for `image/*` and `*/*`, the image picker and the camera.
- Loading parcels needs a bearer token, so either bring back the authenticated `flutter_cache_manager` from the first commit, or have the API hand out short-lived signed URLs so standard image widgets can load them. The cache manager is less moving parts.
- Viewer with pinch zoom, share as a file, save to gallery.

### Rollout

1. API, storage and tests (L).
2. Extension paste and drop (M).
3. Mobile share, picker and viewer (M).
4. Web app drag, drop and paste (M, with the web app).

## 2. Device registry and push notifications

Each client registers itself once: `POST /api/devices { name, platform, pushToken? }`, keyed by a client-generated id and the user. The API stores it as a Marten document and the clients let you rename or remove devices in settings. When `PacketStoreService` commits a packet it already notifies Postgres; add a second consumer that sends a push to the user's other devices: Firebase Cloud Messaging for Android, Web Push (VAPID) for the extension (MV3 service workers can receive `push` events) and the web app. Notifications carry the packet id and type so the tap opens the item; links get an "Open" action, notes a "Copy" action. The registry is also what makes feature 8 possible.

## 3. Android share target

See [UX](ux.md) finding 1. `ACTION_SEND` for text and images, `PROCESS_TEXT` for selected text in any app, a share handler plugin, and a pre-filled sheet or an immediate send with undo.

## 4. Timestamps

`CreatedAt` into `PacketAggregate` and the responses, relative times on tiles, day headers in the feed. Small and unlocks most of the tile redesign.

## 5. Link previews

At link creation, queue a fetch of the page: `og:title`, `og:description`, `og:image`, the favicon. Store the result as a `LinkPreviewFetched` event on the packet stream so the aggregate gains a `Preview`, and emit a `packet-updated` SSE event so clients refresh the tile. Fetch asynchronously (a Marten async projection or a channel-fed hosted service), with hard safeguards because the API is fetching user-supplied URLs: `http`/`https` only, resolve DNS and refuse private and link-local ranges, follow at most three redirects re-checking each hop, 5-second timeout, 1 MB body cap, decode only the `<head>`. Preview images go through the parcel thumbnail pipeline so clients never load third-party images directly.

## 6. Search and filters

Client-side first: filter the loaded pages by text and type in both clients. Then `GET /api/packets?q=`: Marten's full-text index on the snapshot document (`Schema.For<PacketAggregate>().FullTextIndex(...)`) over title, body, URL and preview text, scoped to the user, same cursor paging.

## 7. First-party sessions

Today every client sends a Google ID token (one-hour lifetime) and refreshes it silently through Google. That works, but the web app can't refresh silently without One Tap prompts, there is no revocation or "sign out everywhere", and API keys for a CLI are impossible.

| Option | Pros | Cons |
|---|---|---|
| **Keep Google ID tokens as the API credential** | Stateless, no secrets, already done | One-hour lifetime, silent refresh depends on the Google session, no revocation, no API keys, awkward on the web |
| **Exchange the Google ID token for Passel tokens** (`POST /api/auth/google` returns a short access token and a rotating refresh token, stored hashed, revocable per device) | Long sessions on every client, revocation, device-bound sessions, API keys fall out of the same table, Google stays the identity provider | Signing keys to manage, refresh storage, more code and tests |
| **Cookie session for the web app only** | Simplest for a same-site web app, browser handles storage | Different auth model per client, CSRF handling, nothing for the extension or mobile |

Recommendation: the exchange. Access tokens of 15 minutes signed with a key in Key Vault, refresh tokens of 30 days rotated on use, a `devices` link so a session can be revoked from settings. The clients keep their existing Google flows for the initial sign-in and send the Passel access token afterwards. The API accepts both Google ID tokens and Passel tokens during the migration.

## 8. Send to a specific device

With the registry in place, packets gain an optional `targetDeviceId`. Clients show "Send to phone" or "Send to laptop" as chips, the feed can filter by target, and push goes only to the target. Default stays "all devices".

## 9. Pin, archive, labels

Three more events on the packet stream (`PacketPinned`, `PacketArchived`, `PacketLabelled`), a projection change, and feed filters. Pinned items float to the top; archived items leave the default feed. Labels can wait until there is demand.

## 10. Retention and auto-expiry

A per-user setting ("delete items after 30 days", off by default) plus a per-packet override ("keep"). A nightly job appends `PacketDeleted` to expired streams, which already removes the snapshot and notifies clients. Keeps the feed and storage small without the user doing anything.

## 11. Trash and restore

Event sourcing makes this nearly free: `PacketDeleted` already exists, so add `PacketRestored`, keep the snapshot for deleted packets with a `DeletedAt` instead of removing it, show a Trash view, and purge for real after 30 days. This also gives the undo window a server-side backstop.

## 12. Extension additions

- Side panel hosting the same bundle, so the feed stays open beside the page.
- `commands` for "send current page" and opening the panel; an `omnibox` keyword.
- "Send all tabs in this window" as a collection or as several links.
- Drag and drop links, text and files onto the panel.
- Badge count of unread items since the panel was last open; desktop notifications once push exists.

## 13. Mobile additions

- A home-screen widget showing the latest few items with copy and open actions.
- A Quick Settings tile that sends the clipboard.
- App shortcuts ("Send clipboard", "New note").
- Android App Links for `passel.link/p/{id}` so a shared or pushed link opens the item in the app.

## 14. Account

Settings screen with the signed-in account, devices, theme override, retention, storage usage, data export (a zip of JSON plus parcels, produced by a job and downloaded from a signed URL) and account deletion (a `UserDeleted` event, purge job, and confirmation). Account deletion is required by Google Play for apps with sign-in.

## 15. Public API, keys, CLI, webhooks

Once sessions exist, API keys are a session type without expiry, shown once in settings. A small `passel` CLI (`passel send "text"`, `passel send --file photo.jpg`, `passel list`) in .NET or Go, and outgoing webhooks per user ("POST this URL when a packet arrives") for Zapier and home automation. Publish the OpenAPI document that already exists in development.

## 16. Offline send queue

Both clients fail outright without a network. Queue sends locally and flush on reconnect: `workmanager` on Android, `chrome.alarms` plus storage in the extension, Background Sync in the web app.

## 17. Read and opened state

A `PacketOpened` event when an item is opened on any device, shown as a subtle marker, which makes "unread on this device" possible for badges and widgets.

## 18. Collections

Send several items as one bundle (all tabs, a set of photos). A `Collection` packet type holding child packet ids, or a `collectionId` on packets with grouping in the feed. Wait for parcels.

## 19. Markdown and code notes

Detect code and fenced blocks, render Markdown in notes, syntax highlight in the detail view, copy as plain text. Client-only.

## 20. Desktop app

A Flutter desktop or Tauri app with a global hotkey to send the clipboard or the current selection, a tray icon with the latest items, and clipboard sync between desktops. Big, and overlaps with the extension and web app; only worth it if clipboard sync becomes the product.

## 21. iOS

The `ios/` scaffolding exists but is unconfigured. An iOS app needs Sign in with Apple alongside Google (App Store rule when third-party sign-in is offered), a share extension for the share sheet, and an Apple developer account. Large but it doubles the reachable audience.

## 22. End-to-end encryption

Opt-in client-side encryption with a passphrase-derived key means the server never sees content. It breaks server-side search, previews and thumbnails and complicates recovery, so it should be a deliberate product decision, not a checkbox. Note it as a possible differentiator, design the storage so content fields could be opaque later, and leave it there for now.
