import 'package:flutter_dotenv/flutter_dotenv.dart';

/// Compile-time app configuration with optional `.env` overrides.
///
/// Call [AppConfig.load] once at startup (before `runApp`) to read the
/// bundled `.env` file. Any key present in `.env` takes precedence over
/// the corresponding `--dart-define` value.
///
/// ```
/// # Dev (default):
/// flutter run --flavor dev --dart-define=API_BASE_URL=https://my-tunnel.devtunnels.ms
///
/// # Prod:
/// flutter run --flavor prod
/// ```
class AppConfig {
  AppConfig._();

  /// Load the `.env` asset (safe to call even if the file is empty).
  static Future<void> load() async {
    await dotenv.load(fileName: '.env', mergeWith: {});
  }

  // ── helpers ──────────────────────────────────────────────────────────

  /// Returns the `.env` value for [key] if present, otherwise the
  /// compile-time `--dart-define` value (which may be empty).
  static String _env(String key, {String dartDefine = ''}) =>
      dotenv.maybeGet(key) ?? dartDefine;

  // ── configuration values ─────────────────────────────────────────────

  /// The current flavor: "development" or "production".
  static const String flavor = String.fromEnvironment(
    'FLAVOR',
    defaultValue: 'development',
  );

  static String get serverClientId => _env(
        'SERVER_CLIENT_ID',
        dartDefine: const String.fromEnvironment('SERVER_CLIENT_ID'),
      );

  static String get appClientId => _env(
        'APP_CLIENT_ID',
        dartDefine: const String.fromEnvironment('APP_CLIENT_ID'),
      );

  /// The base URL for the Passel API.
  /// Override at build time:
  /// ```
  /// --dart-define=API_BASE_URL=https://my-custom-host.example.com
  /// ```
  static String get apiBaseUrl => _env(
        'API_BASE_URL',
        dartDefine: const String.fromEnvironment('API_BASE_URL'),
      );

  /// Default API version sent with every request.
  static const String apiVersion = '1';

  /// GET / POST packets endpoint.
  static String get packetsEndpoint => '$apiBaseUrl/api/packets';

  /// Parcels endpoint (upload / download).
  static String get parcelsEndpoint => '$apiBaseUrl/api/parcels';

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


  static bool get isDev => flavor == 'development';

  static bool get isProd => flavor == 'production';
}
