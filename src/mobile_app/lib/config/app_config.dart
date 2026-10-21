import 'package:flutter/services.dart';

/// Compile-time app configuration.
///
/// All values come from `--dart-define` (CI) or `--dart-define-from-file`
/// (local development). Nothing is read from a bundled asset, so a missing
/// `.env` file can neither break the build nor the app start-up, and a stale
/// local file can never override what the build system passes.
///
/// ```
/// # Local dev — copy .env.example to .env (git-ignored) and fill it in:
/// flutter run --flavor development --dart-define-from-file=.env
///
/// # CI / release — pass each value explicitly:
/// flutter build appbundle --flavor production \
///   --dart-define=API_BASE_URL=https://api.example.com \
///   --dart-define=SERVER_CLIENT_ID=... --dart-define=APP_CLIENT_ID=...
/// ```
///
/// If a key is passed both ways, the explicit `--dart-define` wins (the
/// Flutter tool appends command-line defines after the file's).
class AppConfig {
  AppConfig._();

  // ── configuration values ─────────────────────────────────────────────

  /// The current flavor: "development" or "production" (the Android
  /// `productFlavors` in `android/app/build.gradle.kts`).
  ///
  /// Comes from `flutter run/build --flavor <name>` via [appFlavor]; falls
  /// back to "development" when no flavor is given (e.g. in tests).
  static const String flavor = appFlavor ?? 'development';

  static const String serverClientId =
      String.fromEnvironment('SERVER_CLIENT_ID');

  static const String appClientId = String.fromEnvironment('APP_CLIENT_ID');

  /// The base URL for the Passel API.
  /// Override at build time:
  /// ```
  /// --dart-define=API_BASE_URL=https://my-custom-host.example.com
  /// ```
  static const String apiBaseUrl = String.fromEnvironment('API_BASE_URL');

  /// Default API version sent with every request.
  static const String apiVersion = '1';

  /// GET / POST packets endpoint.
  static String get packetsEndpoint => '$apiBaseUrl/api/packets';

  /// Builds a [Uri] for [endpoint] with `api-version` merged in.
  ///
  /// Pass extra [queryParameters] (e.g. `{'cursor': '...'}`) and they will be
  /// combined with the version param. Individual endpoints can override the
  /// version by including `'api-version'` in [queryParameters].
  static Uri apiUri(
    String endpoint, {
    Map<String, String>? queryParameters,
  }) {
    final merged = {
      'api-version': apiVersion,
      if (queryParameters != null) ...queryParameters,
    };
    return Uri.parse(endpoint).replace(queryParameters: merged);
  }

  /// Names of the required values that weren't passed at build time (e.g.
  /// the app was run without `--dart-define-from-file=.env`).
  static List<String> get missingKeys => [
        if (apiBaseUrl.isEmpty) 'API_BASE_URL',
        if (serverClientId.isEmpty) 'SERVER_CLIENT_ID',
      ];

  static bool get isDev => flavor == 'development';

  static bool get isProd => flavor == 'production';
}
