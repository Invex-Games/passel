# Mobile App

`src/mobile_app` is a Flutter app (package `passel_mobileapp`). Android is the supported platform. The iOS project is scaffolding only and isn't configured.

## Structure

| Folder | Contents |
|---|---|
| `lib/config/` | `AppConfig`, the compile-time configuration, and the page shown when a required value is missing |
| `lib/auth/` | Google Sign-In (`google_sign_in` 7), ID token refresh, and HTTP helpers that retry once on a 401 |
| `lib/packets/` | The packet model, repository (HTTP and SSE), offline cache, Riverpod providers and widgets |
| `lib/theme/` | The Material 3 theme, used when the device doesn't provide dynamic colour |

State lives in Riverpod providers. `packetsProvider` serves the cached feed first, then refreshes it. It keeps the whole loaded list in memory across sign-in and an expired session, and drops it when the user's local data is cleared (`userDataGenerationProvider`, bumped on sign-out and when a different account signs in). `packetLiveUpdatesProvider` keeps the server-sent events stream open while the feed is visible, the user is signed in, and the app is in the foreground. Every `resync` event triggers a fresh fetch of the latest packets.

The feed is cached in `shared_preferences`, so it can still be browsed offline. On a cold start `StartupRouter` shows the cached feed as soon as the cache has been read, before Google Sign-In has initialised; the full-screen login page is only shown when there is no cache and the user turns out to be signed out. The cache holds the first page only: at most 50 packets (`PacketCache.maxPackets`) and the cursor that followed the first page, so older pages are fetched again when the user scrolls to them. Writes are debounced by 300 ms (`packetCacheWriteDelayProvider`), so a burst of live events is written once, and the cache is parsed once at start-up by `cachedPacketsProvider`, which the start-up router and the feed share.

Sign-in asks Google for a token for the **web** client (`SERVER_CLIENT_ID`), which the API accepts as its main audience. The token is kept in memory only.

## Configuration

Values are compiled in with `--dart-define` or `--dart-define-from-file`. Nothing is bundled as an asset.

| Key | Required | Value |
|---|---|---|
| `API_BASE_URL` | Yes | The API root, as the device can reach it. Use the dev tunnel URL locally. |
| `SERVER_CLIENT_ID` | Yes | The Google web client id |
| `APP_CLIENT_ID` | No | The Android client id |

Copy `.env.example` to `.env` for local runs. If a value is passed both in the file and with `--dart-define`, the `--dart-define` value wins.

## Flavors

| Flavor | Application id | App name | Signing |
|---|---|---|---|
| `development` | `com.invex.passel.mobileapp.dev` | Passel Dev | The release key if one is configured, otherwise debug keys |
| `production` | `com.invex.passel.mobileapp` | Passel | The release keystore, from the `SIGNING_RELEASE_*` environment variables |

`dotnet atom BuildMobileAppAndroid --mobile-app-flavor production` builds an obfuscated app bundle and packages its symbols. The development flavor builds a debug APK. See [Build Targets](../reference/build-targets.md).
