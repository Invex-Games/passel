import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:http/http.dart' as http;

import '../api/errors.dart';
import 'auth_service.dart';

/// Supplies a Bearer token; [forceRefresh] discards the cached token first
/// (used after the server responded with HTTP 401).
typedef TokenProvider = Future<String> Function({bool forceRefresh});

/// Returns a valid ID token, or throws [SessionExpiredException] if the
/// session can't be refreshed (the auth state is then signed-out).
Future<String> requireToken(Ref ref, {bool forceRefresh = false}) async {
  final authNotifier = ref.read(authServiceProvider.notifier);
  final token = await authNotifier.getIdToken(forceRefresh: forceRefresh);
  if (token == null) {
    throw const SessionExpiredException();
  }
  return token;
}

/// Sends an authenticated request built by [send], retrying **once** with a
/// force-refreshed token if the server answers HTTP 401.
///
/// Transport failures (no connection, a timeout) surface as
/// [NetworkException].  [send] must build a fresh request each time (an
/// `http` request can only be sent once).
Future<T> sendWithAuthRetry<T extends http.BaseResponse>(
  TokenProvider tokenProvider,
  Future<T> Function(String token) send,
) =>
    wrapNetworkErrors(() async {
      final response = await send(await tokenProvider());
      if (response.statusCode != 401) return response;
      // An unread streamed body would keep the rejected connection open.
      if (response is http.StreamedResponse) {
        await response.stream.drain<void>();
      }
      return send(await tokenProvider(forceRefresh: true));
    });

/// Shared HTTP client that does **not** follow redirects.
///
/// The API answers unauthenticated requests with 401, but if a proxy or
/// misconfiguration ever redirects to a sign-in page we want to catch the 3xx
/// in [validateResponse] instead of parsing a 200 HTML page as JSON.
final apiClientProvider = Provider<http.Client>((ref) {
  final inner = http.Client();
  ref.onDispose(inner.close);
  return _NoRedirectClient(inner);
});

/// Throws if [response] indicates a failure.
///
/// Checks:
/// 1. 401 → [SessionExpiredException] (callers retry once before this).
/// 2. 3xx → auth-related error (a redirect to a sign-in page slipped through).
/// 3. Other non-2xx status → [ApiException], carrying the problem details the
///    server sent, if any.
/// 4. A 2xx HTML body (some proxies / CDNs inject auth pages with a 200).
void validateResponse(http.Response response, {String context = 'Request'}) {
  if (response.statusCode >= 200 && response.statusCode < 300) {
    final contentType = response.headers['content-type'] ?? '';
    if (contentType.contains('text/html')) {
      throw Exception(
        '$context received an HTML response instead of JSON. '
        'The session may have expired.',
      );
    }
    return;
  }

  if (response.statusCode == 401) {
    throw SessionExpiredException(
      '$context was rejected (HTTP 401). Please sign in again.',
    );
  }

  if (response.statusCode >= 300 && response.statusCode < 400) {
    throw Exception(
      '$context was redirected (HTTP ${response.statusCode}). '
      'The session may have expired.',
    );
  }

  throw ApiException.fromResponse(response, context: context);
}

// ── Private ─────────────────────────────────────────────────────────────────

/// Wraps an [http.Client] and sets `followRedirects: false` on every request.
class _NoRedirectClient extends http.BaseClient {
  _NoRedirectClient(this._inner);
  final http.Client _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) {
    request.followRedirects = false;
    return _inner.send(request);
  }
}
