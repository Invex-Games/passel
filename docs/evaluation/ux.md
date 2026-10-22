# User Experience

Scope: the two shipped clients (the Chrome extension popup and the Android app) and the API where it limits what the clients can do. Evidence: the real `popup.js` bundle was run in a browser against a stubbed `chrome.*` API with sample data (screenshots in `images/`), the mobile app was reviewed from source, and both were checked against the [HTTP API](../reference/http-api.md).

## Verdict

The core loop works and the hard parts are handled with unusual care: live updates, an offline cache, undoable deletes, silent token refresh and session expiry all behave sensibly. The gaps are in the moments around the loop: getting content into Passel from the phone (no share sheet), knowing that something arrived (no notifications), and finding things later (no timestamps, search or metadata). The two clients also disagree on ordering and vocabulary, so the product feels like two apps rather than one.

Score: 3 / 5 for simple tasks, 2 / 5 for anything beyond them.

## What works well

- Three ways to send from the desktop: the popup's "Send Current Page", typed or pasted text (links are detected automatically, Enter sends), and context menus for links, selections and images.
- The feed appears instantly from cache, then refreshes; a "Live" label and a "Synced X ago" label say how fresh it is.
- Deletes are optimistic with a 4-second undo on both clients, and the extension still commits the delete if the popup closes.
- Session expiry is graceful: the mobile app keeps the cached feed browsable and asks you to sign in from a bottom sheet instead of a dead end; the extension signs out and says why.
- Switching Google accounts clears the previous account's cached packets on both clients.
- Copy and share menus let you pick title, content or both.
- Keyboard: Enter sends, Shift+Enter adds a line, Escape closes menus, icon buttons have `aria-label`s and the copy menu uses `role="menu"` with focus moved to the first item.

## Task walkthroughs

| Task | Extension | Mobile | Friction |
|---|---|---|---|
| Send the page I'm on to my phone | Open popup, one click | n/a | Popup only: no keyboard shortcut, no omnibox, no "send all tabs". |
| Send a link from my phone to my desktop | n/a | Open app, FAB, paste, Push | No Android share sheet entry: you have to copy the URL, switch apps and paste. This is the single biggest gap. |
| Notice that something arrived | Only while the popup is open (SSE in the worker, `live.ts`) | Only while the app is in the foreground (`packetLiveUpdatesProvider`) | No notifications, no badge count. You must go and look. |
| Open a link I was sent | Click title button or the URL; opens a new tab, which closes the popup | Tap the URL; opens externally | Fine, but the popup closing after every open is tiring. A side panel fixes it. |
| Copy a phone number I was sent | Copy button | Copy button | Fine. Untitled items copy directly; titled ones show a menu. |
| Send a photo from my phone | n/a | Not possible | File packets are rejected by the API (`PostPacket.cs`, the `File` branch returns 400). |
| Send an image from a web page | Context menu sends the image URL as a link | n/a | Misleading: it arrives as a link, and `data:` or `blob:` images can't be sent at all (`background.ts`, `packetFromContext`). |
| Find the link I sent last Tuesday | Scroll | Scroll | No timestamps, no search, no filter. |
| Read a long note | Clamped to 6 lines, "Show more" | Entire body rendered (`standard_tile.dart`) | A 64 KB note becomes a screen-long tile on mobile. |
| Use two Google accounts | Account picker on sign-in; cache cleared on switch | Same | Fine. |
| Use it offline | Cached feed shown; sends fail with a clear message | Cached feed shown; sends fail | No outgoing queue. |

## Findings

Severity: **High** blocks or seriously hurts the core job, **Medium** is noticeable friction, **Low** is polish.

1. **No share-sheet entry on Android (High).** `android/app/src/main/AndroidManifest.xml` has no `ACTION_SEND` intent filter and `pubspec.yaml` has no share-receiving plugin, so the only way in is open app, FAB, paste. On a phone, "Share" is how people send things. Fix: add `ACTION_SEND` filters for `text/plain` (and `image/*` once parcels exist), handle them with `receive_sharing_intent` or `share_handler`, and either send immediately with an undo snackbar or open the push sheet pre-filled. Also register as a `PROCESS_TEXT` target so selected text in any app can be sent.

2. **Nothing tells you a packet arrived (High).** Both clients only receive while open and visible, by design of the SSE stream. For an app whose whole job is "it shows up on the other device", this is the expectation gap users will hit first. Options:
   - **Push notifications** (Firebase Cloud Messaging for Android, Web Push for the extension and the future web app): the right answer. Needs a device registry (device id, platform, push token) and a sender in the API, triggered where `PacketStoreService` already sends `pg_notify`. Larger piece of work, see [Features](features.md).
   - **Extension polling with `chrome.alarms`** every minute plus a badge count: cheap and server-free, but delayed and wakes the service worker constantly.
   - Recommendation: build push with the device registry; use the badge count from the popup's own feed state in the meantime.

3. **No timestamps anywhere (High).** The events carry `CreatedAt` (`Domain/Events/TextPacketCreated.cs`) but `PacketAggregate` drops it and neither `GetPacketsResponseBody.Packet` nor the SSE payload includes it, so clients can't show "2 hours ago" or group by day. Fix: add `CreatedAt` to the aggregate and the response records (an additive change, no API version bump), then show relative time on every tile and day headers in the feed.

4. **The clients disagree on ordering (Medium).** The mobile list is reversed, newest at the bottom like a chat (`packet_list.dart`, `reverse: true`), with pull-to-refresh at the bottom and "You've gone back to the beginning!" at the top; the extension shows newest at the top. The same packet is in a different place on each device. Pick one. Newest-at-top is the convention for feeds and matches the extension and the web app to come; if the chat layout is deliberate on mobile, make it a setting.

5. **Internal vocabulary leaks into the UI (Medium).** Mobile says "Push packet", "Push a packet", "Packet deleted", "No packets yet"; the extension says "Send Current Page", "Text sent!", "Packet deleted". Users think in links, notes and files, not packets. Decide on one verb ("Send") and one set of nouns for the UI (link, note, file, or just "item"), keep "packet" and "parcel" as code and API terms, and apply the decision to the store listings too.

6. **Untitled items are labelled "Packet" (Medium).** `popup.ts` `packetTitle()` and `tile_header.dart` fall back to "Packet", which says nothing. Show the host for links (and its favicon), nothing or the first line for notes, and only draw a title row when there is a real title.

7. **Links are shown as raw URLs (Medium).** The body of a link tile is the full URL; a Microsoft Learn URL wraps over four lines (see `images/extension-feed-light.jpg`). Show host and title, keep the URL to one truncated line, and fetch link previews server-side later (see [Features](features.md)).

8. **No search or filtering (Medium).** Infinite scroll is the only navigation. Start with a client-side filter over the loaded pages (both clients already hold them in memory), then add `?q=` to `GET /api/packets` backed by a Marten full-text index on the snapshot document.

9. **Mobile renders whole text bodies (Medium).** `standard_tile.dart` draws `Text(packet.payload)` with no `maxLines`, while the API accepts bodies up to 65,536 characters. Clamp to a few lines with an expand action, or open a detail page on tap, as the extension already does with "Show more".

10. **Raw exceptions reach users on mobile (Medium).** `'Push failed: $e'` (`push_packet_sheet.dart`), `'Refresh failed: $e'` (`packet_list.dart`), `'Couldn't delete packet: $e'` (`delete_packet.dart`) and `'Auth error: ${authState.error}'` (`app.dart`) show strings like `Exception: Push packet failed with HTTP 500`. Map failures to short messages the way the extension's `api.ts` does (`OFFLINE_MESSAGE`, ProblemDetails titles).

11. **Desktop is popup-only (Medium).** The popup is 380 by 600 pixels and closes whenever focus leaves it, including when you open a link from it. Chrome's side panel API (`sidePanel` permission, `chrome.sidePanel.setPanelBehavior`) can host the same `popup.html` bundle persistently beside the page. Keep the popup for quick sends.

12. **No keyboard shortcut or omnibox (Medium).** `static/manifest.json` declares no `commands`. Add a `send-current-page` command with a suggested key and `_execute_action` to open the popup; an `omnibox` keyword (`passel <text>`) is a cheap extra.

13. **"Send image link" is a stand-in for sending the image (Low).** Resolves with parcels. Until then, rename the menu item so it doesn't promise an image.

14. **Two feedback mechanisms in the popup (Low).** Send and sign-in results use an in-flow status bar that pushes the whole layout down (`images/extension-send-status.jpg`); copy and delete use an overlay toast. Use one overlay snackbar and never shift the layout.

15. **The signed-out popup shows a disabled composer (Low).** `images/extension-signed-out.jpg`: a greyed "Send Current Page" and text field sit under the sign-in button. Hide them and use the space for one line on what Passel does and a link to the phone app.

16. **Empty states are a single sentence (Low).** "No packets yet. Send one above, or from another device." and "No packets yet. Pull down to refresh." Replace with three concrete tips (right-click a link, share from your phone, the keyboard shortcut) and an illustration.

17. **Titles exist in the model but can't be typed (Low).** Neither composer has a title field; titles only come from the tab title or a selection. Either add an optional title to both composers or stop showing a title row for notes.

18. **No settings or account surface on mobile (Low).** No version, no account switch, no theme override, no account deletion or data export. Google Play's account deletion policy requires an in-app deletion path for apps with sign-in, so this becomes a blocker for a store listing.

19. **Offline sends fail outright (Low).** Queue them and send on reconnect (mobile: `workmanager`; extension: retry from the worker with `chrome.alarms`).

20. **Accessibility gaps (Low).** The popup's `.md-btn` and `.md-btn-icon` have hover styles but no `:focus-visible` style in `popup.css`, so keyboard focus is only as visible as the browser default. The mobile theme defines medium and high contrast schemes in `theme.dart` but `app.dart` never selects them (`MediaQuery.highContrast` is not consulted). Swipe-to-delete has a button alternative, which is right.

## Steps forward

In rough value-for-effort order. Sizes: S under a day, M a few days, L a week or more.

1. **Add `CreatedAt` to the API responses and show relative time and day headers** (S, API plus both clients). Unlocks 3 and improves 7 and 8.
2. **Android share target and `PROCESS_TEXT`** (S to M, mobile). Fixes 1, the largest gap.
3. **One ordering, one vocabulary** (S, both clients). Fixes 4 and 5; decide before the web app copies either.
4. **Tile redesign** per [UI Design](ui-design.md): host and favicon for links, clamp on mobile, no "Packet" placeholders (M, both clients). Fixes 6, 7, 9.
5. **Extension side panel and commands** (S to M). Fixes 11 and 12.
6. **Error copy, empty states, signed-out state, single snackbar** (S, both clients). Fixes 10, 14, 15, 16.
7. **Client-side filter, then server search** (M). Fixes 8.
8. **Device registry and push notifications** (L, API plus clients). Fixes 2. The registry is also the base for "send to a specific device".
9. **Settings and account page with deletion and export** (M, mobile and web). Fixes 18 and is required for the Play Store.
10. **Parcels end to end** (L). Fixes 13 and the photo scenario; see [Features](features.md).
