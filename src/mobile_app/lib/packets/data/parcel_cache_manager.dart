// Import the IO-specific DefaultCacheManager directly so the analysis server
// sees the httpClientFactory parameter (the conditional-export barrel resolves
// to the stub on some analysis platforms which lacks it).
// ignore: implementation_imports
import 'package:cached_network_image_ce/src/cache/default_cache_manager.dart';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:http/http.dart' as http;

import '../../auth/auth_helpers.dart';
import '../../config/app_config.dart';

// ─── Parcel URL helper ───────────────────────────────────────────────────────

/// Builds the full parcel download URL for a given [parcelId].
String parcelUrl(String parcelId) =>
    AppConfig.apiUri('${AppConfig.parcelsEndpoint}/$parcelId').toString();

// ─── Authenticated HTTP client ───────────────────────────────────────────────

/// An [http.BaseClient] that injects a fresh `Authorization: Bearer <token>`
/// header on every request.
///
/// If the first attempt returns a 401 or a 3xx redirect (which typically means
/// the token has expired and the server is bouncing to a sign-in page), the
/// client refreshes the token and retries **once**.
class _AuthenticatedClient extends http.BaseClient {
  _AuthenticatedClient(this._ref);

  final Ref _ref;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final token = await requireToken(_ref);
    request.headers['Authorization'] = 'Bearer $token';

    final inner = http.Client();
    try {
      final response = await inner.send(request);

      // Retry once on auth failure.
      if (response.statusCode == 401 ||
          (response.statusCode >= 300 && response.statusCode < 400)) {
        debugPrint('[ParcelCacheManager] Got ${response.statusCode}, '
            'refreshing token and retrying…');
        // Drain the failed response body to free resources.
        await response.stream.drain<void>();

        final freshToken = await requireToken(_ref);
        final retry = _copyRequest(request, freshToken);
        final retryClient = http.Client();
        try {
          return await retryClient.send(retry);
        } catch (_) {
          retryClient.close();
          rethrow;
        }
      }

      return response;
    } catch (_) {
      inner.close();
      rethrow;
    }
  }

  /// Creates a new [http.Request] that mirrors [original] but with an updated
  /// Authorization header.  We cannot re-send the same [BaseRequest] because
  /// the `http` package marks it as finalized after the first send.
  static http.BaseRequest _copyRequest(
      http.BaseRequest original, String token) {
    final copy = http.Request(original.method, original.url)
      ..headers.addAll(original.headers)
      ..headers['Authorization'] = 'Bearer $token';
    if (original is http.Request) {
      copy.body = original.body;
    }
    return copy;
  }
}

// ─── Cache manager provider ──────────────────────────────────────────────────

/// A singleton [DefaultCacheManager] configured with authenticated HTTP and
/// generous caching for immutable parcel blobs.
final parcelCacheManagerProvider = Provider<DefaultCacheManager>((ref) {
  final manager = DefaultCacheManager(
    stalePeriod: const Duration(days: 365),
    maxNrOfCacheObjects: 500,
    httpClientFactory: () => _AuthenticatedClient(ref),
  );
  ref.onDispose(() => manager.dispose());
  return manager;
});





