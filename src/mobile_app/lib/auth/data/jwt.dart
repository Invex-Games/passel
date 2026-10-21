import 'dart:convert';

// ─── JWT helpers ─────────────────────────────────────────────────────────────

/// How long before the real expiry a token is already treated as expired.
///
/// Refreshing a little early avoids sending a token that expires while the
/// request is in flight (and absorbs small clock skew between device/server).
const Duration tokenExpiryLeeway = Duration(minutes: 5);

/// Returns the `exp` claim of [token] as a UTC [DateTime], or `null` if the
/// token is malformed or has no numeric `exp` claim.
///
/// The signature is **not** verified — this is only used to decide when to
/// refresh a token we already received from Google; the server does the
/// real validation.
DateTime? jwtExpiry(String token) {
  final parts = token.split('.');
  if (parts.length != 3) return null;

  try {
    final payload = utf8.decode(base64Url.decode(base64Url.normalize(parts[1])));
    final claims = jsonDecode(payload);
    if (claims is! Map<String, dynamic>) return null;

    final exp = claims['exp'];
    if (exp is! num) return null;

    return DateTime.fromMillisecondsSinceEpoch(
      (exp * 1000).round(),
      isUtc: true,
    );
  } catch (_) {
    // Bad base64 / UTF-8 / JSON — treat as malformed.
    return null;
  }
}

/// Whether [token] is expired, or will expire within [leeway] of [now].
///
/// Malformed tokens (no readable `exp`) are treated as expired so the caller
/// refreshes them rather than sending something the server will reject.
bool isTokenExpired(
  String token, {
  required DateTime now,
  Duration leeway = tokenExpiryLeeway,
}) {
  final expiry = jwtExpiry(token);
  if (expiry == null) return true;
  return !now.toUtc().add(leeway).isBefore(expiry);
}
