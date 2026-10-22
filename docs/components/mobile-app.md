# Mobile App

`src/mobile_app` is a Flutter app (package `passel_mobileapp`). Android is the supported platform. The iOS project is scaffolding only and isn't configured.

## Structure

| Folder | Contents |
|---|---|
| `lib/config/` | `AppConfig`, the compile-time configuration, and the page shown when a required value is missing |
| `lib/api/` | The error types (`ApiException`, `NetworkException`, `SessionExpiredException`, `SignInException`) and `describeError`, which turns any of them into user-facing text |
| `lib/auth/` | Google Sign-In (`google_sign_in` 7), ID token refresh, the `DEV_USER` sign-in through the API's `Dev` scheme (`authorization.dart` maps a token to its `Authorization` header), the API HTTP client (`ApiClient`: never follows redirects, sends the tunnel token when there is one) and helpers that retry once on a 401 |
| `lib/packets/` | The packet model, repository (HTTP and SSE), offline cache, Riverpod providers and widgets |
| `lib/theme/` | The Material 3 theme, used when the device doesn't provide dynamic colour |

State lives in Riverpod providers. `packetsProvider` serves the cached feed first, then refreshes it. It keeps the whole loaded list in memory across sign-in and an expired session, and drops it when the user's local data is cleared (`userDataGenerationProvider`, bumped on sign-out and when a different account signs in). `packetLiveUpdatesProvider` keeps the server-sent events stream open while the feed is visible, the user is signed in, and the app is in the foreground. Every `resync` event triggers a fresh fetch of the latest packets: immediately for the first one after the stream (re)starts, after a random delay of up to 3 seconds for later ones, and reconnects back off exponentially with full jitter (a random share of the backoff, up to a minute), so clients that lose the server together don't all come back in the same instant.

The feed is cached in `shared_preferences`, so it can still be browsed offline. On a cold start `StartupRouter` shows the cached feed as soon as the cache has been read, before Google Sign-In has initialised; the full-screen login page is only shown when there is no cache and the user turns out to be signed out. The cache holds the first page only: at most 50 packets (`PacketCache.maxPackets`) and the cursor that followed the first page, so older pages are fetched again when the user scrolls to them. Writes are debounced by 300 ms (`packetCacheWriteDelayProvider`), so a burst of live events is written once, and the cache is parsed once at start-up by `cachedPacketsProvider`, which the start-up router and the feed share.

Sign-in asks Google for a token for the **web** client (`SERVER_CLIENT_ID`), which the API accepts as its main audience. The token is kept in memory only.

## Errors

`validateResponse` (in `lib/auth/auth_helpers.dart`) turns a failure response into an `ApiException` with the status and, when the server sent `application/problem+json`, its `title`, `detail` and the validation messages keyed by field. Transport failures thrown by `package:http` and `dart:io` (no connection, timeouts) become a `NetworkException`, a 401 that survives the token retry a `SessionExpiredException`, and Google Sign-In failures a `SignInException` whose message is already written for the user. Every snackbar and error row (push, refresh, delete, the feed's error state, the login page and sheet, the start-up auth error) shows `describeError(error)`: client errors (4xx) use the server's `detail` or field messages, everything else a short generic sentence, so no exception text reaches the screen.

## Configuration

Values are compiled in with `--dart-define` or `--dart-define-from-file`. Nothing is bundled as an asset.

| Key | Required | Value |
|---|---|---|
| `API_BASE_URL` | Yes | The API root, as the device can reach it: `http://localhost:5218` for a phone on USB (with `adb reverse`), `http://10.0.2.2:5218` for an emulator, or the dev tunnel URL. See [Running Locally](../getting-started/running-locally.md#mobile-app). |
| `SERVER_CLIENT_ID` | Unless `DEV_USER` is set | The Google web client id |
| `APP_CLIENT_ID` | No | The Android client id |
| `TUNNEL_ACCESS_TOKEN` | No | A Dev Tunnels access token, for a tunnel that doesn't allow anonymous access. Development flavor only. |
| `DEV_USER` | No | Signs in as this user through the API's Development-only `Dev` scheme instead of Google, so no Google client is needed. The login page can sign in as any other user, and each user is a separate account. Development flavor only. See [Run without Google](../getting-started/running-locally.md#run-without-google). |

Copy `.env.example` to `.env` for local runs. If a value is passed both in the file and with `--dart-define`, the `--dart-define` value wins.

`AppConfig` checks the values at start-up and shows `ConfigErrorPage` instead of the app when one is missing or can't be used in the flavor: the production flavor needs an `https://` API URL and refuses a tunnel token and a development user, and the development flavor allows an `http://` URL only to `localhost`, `127.0.0.1` and `10.0.2.2`, the hosts its network security configuration permits cleartext to.

## Flavors

| Flavor | Application id | App name | Signing |
|---|---|---|---|
| `development` | `com.invex.passel.mobileapp.dev` | Passel Dev | The release key if one is configured, otherwise debug keys |
| `production` | `com.invex.passel.mobileapp` | Passel | The release keystore, from the `SIGNING_RELEASE_*` environment variables |

The development flavor also carries a network security configuration (`android/app/src/development/res/xml/network_security_config.xml`, applied by that flavor's manifest) that permits plain HTTP to `localhost`, `127.0.0.1` and `10.0.2.2`, so a phone on USB and an emulator reach the local API without a tunnel or a trusted certificate. The production flavor has no such configuration and keeps the platform default, which blocks cleartext everywhere.

`dotnet atom BuildMobileAppAndroid --mobile-app-flavor production` builds an obfuscated app bundle and packages its symbols. The development flavor builds a debug APK. See [Build Targets](../reference/build-targets.md).
