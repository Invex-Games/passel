import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:http/http.dart' as http;

import 'auth_service.dart';

/// Returns a valid ID token or signs out and throws if the session expired.
///
/// Shared between [PacketsNotifier] and [ParcelService] so both use the same
/// token-refresh / sign-out logic.
Future<String> requireToken(Ref ref) async {
  final authNotifier = ref.read(authServiceProvider.notifier);
  final token = await authNotifier.getIdToken();
  if (token == null) {
    throw Exception('Session expired — please sign in again.');
  }
  return token;
}

/// Shared HTTP client that does **not** follow redirects.
///
/// When the bearer token is expired the API may return a 3xx redirect to an
/// identity-provider sign-in page. If the client follows it, we end up with a
/// 200 HTML response that fails to parse as JSON.  Disabling auto-redirect lets
/// us catch the 3xx status code in [validateResponse] and surface a clear
/// "session expired" error instead.
final apiClientProvider = Provider<http.Client>((ref) {
  final inner = http.Client();
  ref.onDispose(inner.close);
  return _NoRedirectClient(inner);
});

/// Throws a descriptive [Exception] if [response] indicates a failure.
///
/// Checks:
/// 1. Non-2xx status → throws immediately.
/// 2. If the response body looks like HTML (e.g. a sign-in page served after a
///    redirect that slipped through), throws an auth-related message.
void validateResponse(http.Response response, {String context = 'Request'}) {
  if (response.statusCode >= 200 && response.statusCode < 300) {
    // Successful status — still guard against HTML bodies (some proxies /
    // CDNs inject auth pages with a 200 status).
    final contentType = response.headers['content-type'] ?? '';
    if (contentType.contains('text/html')) {
      throw Exception(
        '$context received an HTML response instead of JSON. '
        'The session may have expired.',
      );
    }
    return;
  }

  if (response.statusCode >= 300 && response.statusCode < 400) {
    throw Exception(
      '$context was redirected (HTTP ${response.statusCode}). '
      'The session may have expired.',
    );
  }

  throw Exception('$context failed with HTTP ${response.statusCode}');
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

