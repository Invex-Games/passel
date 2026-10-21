import 'dart:async';

import 'package:google_sign_in/google_sign_in.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'jwt.dart';

/// Encapsulates all Google Sign-In interactions.
///
/// The Riverpod [AuthService] delegates to this class, keeping the notifier
/// a thin UI-state adapter.  Accepts an optional [GoogleSignIn] instance and
/// [clock] for testability.
///
/// [onUserDataInvalidated] is awaited whenever locally stored user data must
/// be discarded — on explicit [signOut] and when a *different* account signs
/// in than the one that last used the app.  It runs before the auth-state
/// change is emitted, so listeners never observe the previous user's data.
class AuthRepository {
  AuthRepository({
    GoogleSignIn? googleSignIn,
    DateTime Function()? clock,
    Future<void> Function()? onUserDataInvalidated,
  })  : _googleSignIn = googleSignIn ?? GoogleSignIn.instance,
        _clock = clock ?? DateTime.now,
        _onUserDataInvalidated = onUserDataInvalidated;

  /// SharedPreferences key holding the id of the last signed-in account, so
  /// an account change can be detected across app restarts.
  static const String lastUserIdKey = 'auth_last_user_id';

  final GoogleSignIn _googleSignIn;
  final DateTime Function() _clock;
  final Future<void> Function()? _onUserDataInvalidated;

  String? _idToken;
  StreamSubscription<GoogleSignInAuthenticationEvent>? _authSub;

  /// In-flight silent refresh, shared by concurrent [getIdToken] callers.
  Future<String?>? _refreshing;

  /// Serializes sign-in handling: the same sign-in can arrive both from the
  /// event stream and from the future returned by the Google call.
  Future<void> _signInQueue = Future<void>.value();

  /// Broadcast stream of sign-in state changes (`true` = signed in).
  ///
  /// Call [initialize] before listening.
  final _controller = StreamController<bool>.broadcast();

  Stream<bool> get authStateChanges => _controller.stream;

  /// Whether the user currently holds an ID token (possibly near expiry —
  /// [getIdToken] refreshes it transparently).
  bool get isSignedIn => _idToken != null;

  /// Current cached ID token, or `null` if not authenticated.  Prefer
  /// [getIdToken], which refreshes expired tokens.
  String? get idToken => _idToken;

  // ── Lifecycle ──────────────────────────────────────────────────────────────

  /// Initializes the Google Sign-In SDK and starts listening to auth events.
  ///
  /// Must be called once before any other method.  Returns `true` if a
  /// previous session was silently restored.
  Future<bool> initialize({
    required String? clientId,
    required String serverClientId,
  }) async {
    await _googleSignIn.initialize(
      clientId: clientId,
      serverClientId: serverClientId,
    );

    _authSub?.cancel();
    _authSub = _googleSignIn.authenticationEvents.listen(
      (event) {
        switch (event) {
          case GoogleSignInAuthenticationEventSignIn(:final user):
            unawaited(_handleSignIn(user));
          case GoogleSignInAuthenticationEventSignOut():
            _setSignedOut();
        }
      },
      // Errors are surfaced through the futures of the calls that caused them
      // (signIn / silent refresh), which decide the resulting auth state.
      onError: (_) {},
    );

    // Attempt silent sign-in to restore a previous session.
    final token = await _silentSignIn();
    return token != null;
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  /// Interactive sign-in. Returns `true` on success, `false` if cancelled.
  ///
  /// Throws for non-cancellation errors so the UI can show a meaningful message.
  Future<bool> signIn() async {
    if (!_googleSignIn.supportsAuthenticate()) {
      throw Exception('Google Sign-In is not supported on this device.');
    }
    try {
      final account =
          await _googleSignIn.authenticate(scopeHint: ['email', 'profile']);
      await _handleSignIn(account);
      return true;
    } on GoogleSignInException catch (e) {
      if (e.code == GoogleSignInExceptionCode.canceled) {
        return false;
      }
      throw Exception('Google Sign-In failed (${e.code}): $e');
    }
  }

  /// Signs out and clears the signed-in user's locally stored data.
  ///
  /// Uses [GoogleSignIn.signOut] rather than `disconnect()` so the app's
  /// authorization is kept and the user isn't asked for consent again on the
  /// next sign-in.
  Future<void> signOut() async {
    await _invalidateUserData();
    await _writeLastUserId(null);
    try {
      await _googleSignIn.signOut();
    } catch (_) {}
    _setSignedOut();
  }

  /// Returns a usable ID token, silently refreshing it if it is missing,
  /// malformed, or expires within [tokenExpiryLeeway].
  ///
  /// Pass [forceRefresh] after the server rejected the current token (HTTP
  /// 401) to discard it and fetch a new one.
  ///
  /// Returns `null` — and emits a signed-out state — if no valid token can be
  /// obtained, so the UI can prompt the user to sign in again.
  Future<String?> getIdToken({bool forceRefresh = false}) async {
    final current = _idToken;
    if (forceRefresh) {
      _idToken = null;
    } else if (current != null &&
        !isTokenExpired(current, now: _clock())) {
      return current;
    }
    return _refreshing ??= _refresh(rejected: forceRefresh ? current : null)
        .whenComplete(() => _refreshing = null);
  }

  /// Tears down subscriptions.  Call when the owner is disposed.
  void dispose() {
    _authSub?.cancel();
    _controller.close();
  }

  // ── Private ────────────────────────────────────────────────────────────────

  /// Silent refresh used by [getIdToken].
  ///
  /// [rejected] is a token the server refused; it is never handed out again.
  Future<String?> _refresh({String? rejected}) async {
    final previous = _idToken ?? rejected;
    final token = await _silentSignIn();
    if (token != null && token != rejected) return token;

    // Refresh failed.  A token that is near (but not past) expiry is still
    // usable, so don't sign the user out over e.g. a flaky network.
    if (previous != null &&
        previous != rejected &&
        !isTokenExpired(previous, now: _clock(), leeway: Duration.zero)) {
      _idToken = previous;
      return previous;
    }

    _setSignedOut();
    return null;
  }

  /// Runs Google's lightweight (no/minimal UI) authentication and returns the
  /// resulting non-expired ID token, or `null`.
  Future<String?> _silentSignIn() async {
    try {
      final account = await _googleSignIn.attemptLightweightAuthentication();
      if (account != null) await _handleSignIn(account);
    } catch (_) {
      // Silent sign-in failed — the caller decides what that means.
    }
    final token = _idToken;
    if (token == null) return null;
    return isTokenExpired(token, now: _clock(), leeway: Duration.zero)
        ? null
        : token;
  }

  /// Applies a successful sign-in from any source (interactive, silent, or
  /// the authentication event stream).  Idempotent.
  Future<void> _handleSignIn(GoogleSignInAccount account) =>
      _signInQueue = _signInQueue
          .catchError((Object _) {})
          .then((_) => _applySignIn(account));

  Future<void> _applySignIn(GoogleSignInAccount account) async {
    final token = account.authentication.idToken;
    if (token == null) return;

    final lastUserId = await _readLastUserId();
    final accountChanged = lastUserId != null && lastUserId != account.id;
    if (accountChanged) {
      await _invalidateUserData();
      // Make sure listeners observe a transition even if they only track the
      // signed-in flag, so per-user state is rebuilt for the new account.
      if (_idToken != null) _emit(false);
    }
    if (lastUserId != account.id) await _writeLastUserId(account.id);

    _idToken = token;
    _emit(true);
  }

  void _setSignedOut() {
    _idToken = null;
    _emit(false);
  }

  void _emit(bool signedIn) {
    if (!_controller.isClosed) _controller.add(signedIn);
  }

  Future<void> _invalidateUserData() async {
    try {
      await _onUserDataInvalidated?.call();
    } catch (_) {
      // Clearing local data must never block sign-out / sign-in.
    }
  }

  Future<String?> _readLastUserId() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      return prefs.getString(lastUserIdKey);
    } catch (_) {
      return null;
    }
  }

  Future<void> _writeLastUserId(String? userId) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      if (userId == null) {
        await prefs.remove(lastUserIdKey);
      } else {
        await prefs.setString(lastUserIdKey, userId);
      }
    } catch (_) {}
  }
}
