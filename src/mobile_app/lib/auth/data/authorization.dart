/// How a token becomes the `Authorization` header of an API request.
///
/// Tokens are Google ID tokens, sent as `Bearer <token>`. The one exception
/// is the development flavor with `DEV_USER` set: the app then holds a token
/// it made itself, marked with [devTokenPrefix], and sends `Dev <user>`,
/// which the API accepts only when it runs in Development. Different users
/// are different accounts to the API, so per-user behaviour can be tested
/// by signing in as another name. See
/// `docs/getting-started/running-locally.md`.
library;

/// Marks a token the app issued for the API's `Dev` scheme.
const String devTokenPrefix = 'dev-user:';

/// What the API accepts as a development user id: 1 to 255 ASCII letters,
/// digits or `-._@+`, so a name, an email address or a Google-style number.
final RegExp devUserPattern = RegExp(r'^[A-Za-z0-9._@+\-]{1,255}$');

/// The problem shown for a user id that fails [devUserPattern].
const String devUserRule =
    'must be 1 to 255 letters, digits or -._@+ characters';

/// Whether [user] is a user id the API's `Dev` scheme accepts.
bool isValidDevUser(String user) => devUserPattern.hasMatch(user);

/// The token the app holds for the development user [user].
String devToken(String user) => '$devTokenPrefix$user';

/// The user of a [devToken], or `null` for a Google ID token.
String? devUserOf(String token) => token.startsWith(devTokenPrefix)
    ? token.substring(devTokenPrefix.length)
    : null;

/// `Dev <user>` for a [devToken], otherwise `Bearer <token>`.
String authorizationHeader(String token) {
  final user = devUserOf(token);
  return user == null ? 'Bearer $token' : 'Dev $user';
}
