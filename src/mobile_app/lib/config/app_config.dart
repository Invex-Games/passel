import 'package:flutter/services.dart';

import '../auth/data/authorization.dart';

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

  /// The base URL for the Passel API, as this device can reach it.
  ///
  /// The production flavor requires `https://`. The development flavor also
  /// accepts `http://` to one of [cleartextHosts]: `localhost` for a phone on
  /// USB with `adb reverse tcp:5218 tcp:5218`, `10.0.2.2` for an emulator.
  /// Override at build time:
  /// ```
  /// --dart-define=API_BASE_URL=https://my-custom-host.example.com
  /// ```
  static const String apiBaseUrl = String.fromEnvironment('API_BASE_URL');

  /// A Dev Tunnels access token, sent as `X-Tunnel-Authorization` with every
  /// request when the API is reached through a tunnel that doesn't allow
  /// anonymous access. Development flavor only; empty when unset.
  static const String tunnelAccessToken =
      String.fromEnvironment('TUNNEL_ACCESS_TOKEN');

  /// Turns on signing in without Google, through the API's `Dev` scheme
  /// (`Authorization: Dev <user>`), which the API only accepts in
  /// Development, and names the user signed in by default. The login page
  /// then lets you sign in as any other user, and different users are
  /// different accounts. Development flavor only; empty when unset. With it
  /// set, `SERVER_CLIENT_ID` isn't needed and Google sign-in never runs.
  static const String devUser = String.fromEnvironment('DEV_USER');

  /// Whether this build signs in through the `Dev` scheme instead of Google.
  static bool get usesDevUser => isDev && devUser.isNotEmpty;

  /// The hosts the development flavor may reach over plain `http://`: the
  /// developer's machine as seen through `adb reverse` or from an emulator.
  /// Mirrors `android/app/src/development/res/xml/network_security_config.xml`,
  /// which is what actually permits the cleartext traffic.
  static const List<String> cleartextHosts = [
    'localhost',
    '127.0.0.1',
    '10.0.2.2',
  ];

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

  // ── validation ───────────────────────────────────────────────────────

  /// Names of the required values that weren't passed at build time (e.g.
  /// the app was run without `--dart-define-from-file=.env`).
  static List<String> get missingKeys => [
        if (apiBaseUrl.isEmpty) 'API_BASE_URL',
        // A development user signs in without Google, so no client id then.
        if (serverClientId.isEmpty && !usesDevUser) 'SERVER_CLIENT_ID',
      ];

  /// Values that were passed at build time but can't be used in this flavor,
  /// keyed by name, each with the reason.
  static Map<String, String> get invalidValues => validate(
        flavor: flavor,
        apiBaseUrl: apiBaseUrl,
        tunnelAccessToken: tunnelAccessToken,
        devUser: devUser,
      );

  /// Whether the build carries everything the app needs. When it doesn't,
  /// [missingKeys] and [invalidValues] say what is wrong.
  static bool get isValid => missingKeys.isEmpty && invalidValues.isEmpty;

  /// The checks behind [invalidValues], with the values passed in so they
  /// can be exercised for either flavor.
  ///
  /// The production flavor only talks to the API over `https://`, and never
  /// carries a tunnel token or a development user (development credentials
  /// that would otherwise be compiled into a store build). The development
  /// flavor may use `http://` for [cleartextHosts] only, since Android blocks
  /// cleartext to anything else and the failure would otherwise look like a
  /// network error.
  static Map<String, String> validate({
    required String flavor,
    required String apiBaseUrl,
    required String tunnelAccessToken,
    String devUser = '',
  }) {
    final production = flavor == 'production';
    final apiBaseUrlProblem = apiBaseUrl.isEmpty
        ? null
        : _apiBaseUrlProblem(apiBaseUrl, production: production);

    return {
      if (apiBaseUrlProblem != null) 'API_BASE_URL': apiBaseUrlProblem,
      if (production && tunnelAccessToken.isNotEmpty)
        'TUNNEL_ACCESS_TOKEN':
            'must not be set in the production flavor; it is a development '
                'tunnel credential',
      if (production && devUser.isNotEmpty)
        'DEV_USER': 'must not be set in the production flavor; it signs in '
            'through the development scheme of the API without Google',
      if (!production && devUser.isNotEmpty && !isValidDevUser(devUser))
        'DEV_USER': devUserRule,
    };
  }

  static String? _apiBaseUrlProblem(String url, {required bool production}) {
    final uri = Uri.tryParse(url);
    final scheme = uri?.scheme;
    if (scheme == 'https') return null;
    if (production) {
      return 'must be an https:// URL in the production flavor';
    }
    final hosts = cleartextHosts.join(', ');
    if (scheme == 'http') {
      final host = uri!.host;
      if (cleartextHosts.contains(host)) return null;
      return 'may use http:// only for $hosts (a phone on USB or an '
          'emulator); $host needs https://';
    }
    return 'must be an https:// URL, or an http:// URL to $hosts';
  }

  static bool get isDev => flavor == 'development';

  static bool get isProd => flavor == 'production';
}
