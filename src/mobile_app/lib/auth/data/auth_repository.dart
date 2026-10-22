import 'dart:async';

import 'package:google_sign_in/google_sign_in.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../../api/errors.dart';
import 'account_info.dart';
import 'authorization.dart';
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
///
/// With a non-empty [devUser] (the `DEV_USER` dart-define, development
/// flavor only) Google is never called: the repository signs in through the
/// API's `Dev` scheme instead, see [authorizationHeader].  [devUser] is the
/// user signed in by default; [signIn] can name any other, and the one last
/// used is remembered across restarts.  Each user is its own account, with
/// the same cached-data bookkeeping as a Google account change.
class AuthRepository {
  AuthRepository({
    GoogleSignIn? googleSignIn,
    DateTime Function()? clock,
    Future<void> Function()? onUserDataInvalidated,
    String devUser = '',
  })  : _googleSignIn = googleSignIn ?? GoogleSignIn.instance,
        _clock = clock ?? DateTime.now,
        _onUserDataInvalidated = onUserDataInvalidated,
        _defaultDevUser = devUser;

  /// SharedPreferences key holding the id of the last signed-in account, so
  /// an account change can be detected across app restarts.
  static const String lastUserIdKey = 'auth_last_user_id';

  /// SharedPreferences key holding the development user last signed in as,
  /// so a restart signs in as the same one.
  static const String devUserKey = 'auth_dev_user';

  final GoogleSignIn _googleSignIn;
  final DateTime Function() _clock;
  final Future<void> Function()? _onUserDataInvalidated;

  /// The development user signed in by default, or empty for Google sign-in.
  final String _defaultDevUser;

  bool get _devMode => _defaultDevUser.isNotEmpty;

  String? _idToken;
  AccountInfo? _account;
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

  /// The development user signed in right now, or `null` when signed out or
  /// signed in through Google.
  String? get currentDevUser {
    final token = _idToken;
    return token == null ? null : devUserOf(token);
  }

  /// The Google account signed in right now, or `null` when signed out or
  /// signed in as a development user.
  AccountInfo? get currentAccount => _account;

  // ── Lifecycle ──────────────────────────────────────────────────────────────

  /// Initializes the Google Sign-In SDK and starts listening to auth events.
  ///
  /// Must be called once before any other method.  Returns `true` if a
  /// previous session was silently restored.  Throws [SignInException] if
  /// the SDK can't be set up (a misconfigured build).
  Future<bool> initialize({
    required String? clientId,
    required String serverClientId,
  }) async {
    if (_devMode) {
      await _applyDevSignIn(await _readDevUser() ?? _defaultDevUser);
      return true;
    }

    try {
      await _googleSignIn.initialize(
        clientId: clientId,
        serverClientId: serverClientId,
      );
    } on GoogleSignInException catch (e) {
      throw SignInException(describeSignInFailure(e.code), e);
    }

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
  /// With a development user configured, signs in as [devUser] (any name the
  /// API accepts), else as the user last signed in, else the default; Google
  /// is never involved and [devUser] is ignored otherwise.
  ///
  /// Throws [SignInException], whose message is written for the user, for
  /// other failures.
  Future<bool> signIn({String? devUser}) async {
    if (_devMode) {
      final user = devUser?.trim() ?? await _readDevUser() ?? _defaultDevUser;
      if (!isValidDevUser(user)) {
        throw const SignInException('A development user $devUserRule.');
      }
      await _applyDevSignIn(user);
      return true;
    }

    if (!_googleSignIn.supportsAuthenticate()) {
      throw const SignInException(
        'Google Sign-In is not available on this device.',
      );
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
      throw SignInException(describeSignInFailure(e.code), e);
    }
  }

  /// User-facing text for a Google Sign-In failure with [code].
  static String describeSignInFailure(GoogleSignInExceptionCode code) =>
      switch (code) {
        GoogleSignInExceptionCode.interrupted =>
          'Sign-in was interrupted. Please try again.',
        GoogleSignInExceptionCode.clientConfigurationError ||
        GoogleSignInExceptionCode.providerConfigurationError =>
          'Google Sign-In is not set up correctly for this build of Passel.',
        GoogleSignInExceptionCode.uiUnavailable =>
          'Google Sign-In could not be shown. Please try again.',
        _ => 'Google Sign-In failed. Please try again.',
      };

  /// Signs out and clears the signed-in user's locally stored data.
  ///
  /// Uses [GoogleSignIn.signOut] rather than `disconnect()` so the app's
  /// authorization is kept and the user isn't asked for consent again on the
  /// next sign-in.
  Future<void> signOut() async {
    await _invalidateUserData();
    await _writeLastUserId(null);
    if (_devMode) {
      await _writeDevUser(null);
    } else {
      try {
        await _googleSignIn.signOut();
      } catch (_) {}
    }
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
    // A development token never expires and can't be refreshed; a rejection
    // means the API isn't running in Development, which a refresh won't fix.
    if (_devMode) {
      if (_idToken == null) {
        await _applyDevSignIn(await _readDevUser() ?? _defaultDevUser);
      }
      return _idToken;
    }

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

    await _applyAccount(account.id);
    _idToken = token;
    _account = AccountInfo(
      email: account.email,
      displayName: _nameOrNull(account.displayName),
    );
    _emit(true);
  }

  /// [name], unless it is blank: Google has no name for some accounts.
  static String? _nameOrNull(String? name) {
    final trimmed = name?.trim();
    return trimmed == null || trimmed.isEmpty ? null : trimmed;
  }

  /// Signs in as the development user [user]: no Google call, a token the
  /// API's `Dev` scheme accepts, and the same account-change bookkeeping as
  /// a Google sign-in, so a different user never sees the previous one's
  /// data.  The user is remembered for the next start.
  Future<void> _applyDevSignIn(String user) async {
    await _applyAccount('dev:$user');
    await _writeDevUser(user);
    _idToken = devToken(user);
    _emit(true);
  }

  /// Records that [userId] is signing in, clearing the previous account's
  /// data first when it is a different one.
  Future<void> _applyAccount(String userId) async {
    final lastUserId = await _readLastUserId();
    final accountChanged = lastUserId != null && lastUserId != userId;
    if (accountChanged) {
      await _invalidateUserData();
      // Make sure listeners observe a transition even if they only track the
      // signed-in flag, so per-user state is rebuilt for the new account.
      if (_idToken != null) _emit(false);
    }
    if (lastUserId != userId) await _writeLastUserId(userId);
  }

  void _setSignedOut() {
    _idToken = null;
    _account = null;
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

  Future<String?> _readLastUserId() => _readString(lastUserIdKey);

  Future<void> _writeLastUserId(String? userId) =>
      _writeString(lastUserIdKey, userId);

  Future<String?> _readDevUser() => _readString(devUserKey);

  Future<void> _writeDevUser(String? user) => _writeString(devUserKey, user);

  Future<String?> _readString(String key) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      return prefs.getString(key);
    } catch (_) {
      return null;
    }
  }

  Future<void> _writeString(String key, String? value) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      if (value == null) {
        await prefs.remove(key);
      } else {
        await prefs.setString(key, value);
      }
    } catch (_) {}
  }
}
