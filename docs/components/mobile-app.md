# Mobile App

`src/mobile_app` is a Flutter app (package `passel_mobileapp`). Android is the supported platform. The iOS project is scaffolding only and isn't configured.

## Structure

| Folder | Contents |
|---|---|
| `lib/config/` | `AppConfig`, the compile-time configuration, and the page shown when a required value is missing |
| `lib/api/` | The error types (`ApiException`, `NetworkException`, `SessionExpiredException`, `SignInException`) and `describeError`, which turns any of them into user-facing text |
| `lib/auth/` | Google Sign-In (`google_sign_in` 7), ID token refresh, the `DEV_USER` sign-in through the API's `Dev` scheme (`authorization.dart` maps a token to its `Authorization` header), the API HTTP client (`ApiClient`: never follows redirects, sends the tunnel token when there is one) and helpers that retry once on a 401 |
| `lib/packets/` | The packet model, repository (HTTP and SSE), offline cache, Riverpod providers and widgets |
| `lib/settings/` | The settings page, the theme choice and the app version |
| `lib/theme/` | The Material 3 theme, used when the device doesn't provide dynamic colour |

State lives in Riverpod providers. `packetsProvider` serves the cached feed first, then refreshes it. It keeps the whole loaded list in memory across sign-in and an expired session, and drops it when the user's local data is cleared (`userDataGenerationProvider`, bumped on sign-out and when a different account signs in). `packetLiveUpdatesProvider` keeps the server-sent events stream open while the feed is visible, the user is signed in, and the app is in the foreground. Every `resync` event triggers a fresh fetch of the latest packets: immediately for the first one after the stream (re)starts, after a random delay of up to 3 seconds for later ones, and reconnects back off exponentially with full jitter (a random share of the backoff, up to a minute), so clients that lose the server together don't all come back in the same instant.

The feed is cached in `shared_preferences`, so it can still be browsed offline. On a cold start `StartupRouter` shows the cached feed as soon as the cache has been read, before Google Sign-In has initialised; the full-screen login page is only shown when there is no cache and the user turns out to be signed out. The cache holds the first page only: at most 50 packets (`PacketCache.maxPackets`) and the cursor that followed the first page, so older pages are fetched again when the user scrolls to them. Writes are debounced by 300 ms (`packetCacheWriteDelayProvider`), so a burst of live events is written once, and the cache is parsed once at start-up by `cachedPacketsProvider`, which the start-up router and the feed share.

Sign-in asks Google for a token for the **web** client (`SERVER_CLIENT_ID`), which the API accepts as its main audience. The token is kept in memory only.

## Errors

`validateResponse` (in `lib/auth/auth_helpers.dart`) turns a failure response into an `ApiException` with the status and, when the server sent `application/problem+json`, its `title`, `detail` and the validation messages keyed by field. Transport failures thrown by `package:http` and `dart:io` (no connection, timeouts) become a `NetworkException`, a 401 that survives the token retry a `SessionExpiredException`, and Google Sign-In failures a `SignInException` whose message is already written for the user. Every snackbar and error row (push, refresh, delete, the feed's error state, the login page and sheet, the start-up auth error) shows `describeError(error)`: client errors (4xx) use the server's `detail` or field messages, everything else a short generic sentence, so no exception text reaches the screen.

## Sharing into the app

The app is an Android share target. `AndroidManifest.xml` declares an activity alias, `SendToPassel`, on `MainActivity` with intent filters for `ACTION_SEND` with `text/plain` (the share sheet) and `ACTION_PROCESS_TEXT` (the text selection menu); its label, "Send to Passel" (or "Send to Passel Dev"), comes from the flavor's `resValue`, so the launcher label is untouched. `MainActivity.kt` takes the shared text from the intent and hands it to Dart over the `passel/share` method channel: text that arrives before Dart is ready waits until `ShareIntent.takePendingText` asks for it, text that arrives while the app runs comes through `ShareIntent.sharedText`. `PacketFeedPage` opens the send sheet filled in with the text; the user sends or edits it. No share-receiving plugin is involved. Images wait for parcels.

## Settings

The feed page's app bar has a settings action beside the sign-in or sign-out action; it opens `SettingsPage` (`lib/settings/`), which works signed in and signed out.

- **Account** shows who is signed in: the Google account's name and email (`AuthService.currentAccount`, an `AccountInfo` the repository takes from the Google sign-in and drops on sign-out) or the development user (`currentDevUser`), with a Sign out tile. Signed out it offers Sign in, through the same sheet as the feed.
- **Appearance** chooses the theme: system default, light or dark. `themeModeProvider` (`theme_mode_setting.dart`) holds the choice, `MyApp` passes it to `MaterialApp` as the `themeMode` on top of the device's dynamic colours, and it is saved in `shared_preferences` (`settings_theme_mode`) so it survives a restart; until the saved choice has been read the app follows the device.
- **About** shows the version and build number (`package_info_plus`, read once by `appVersionProvider`), in a development build the flavor and the API URL, and the privacy policy and terms of service at `passel.link/privacy` and `passel.link/terms`. Those two tiles stay disabled (`legalPagesPublished` in `settings_page.dart`) until roadmap package 1.E3 publishes the pages.

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
