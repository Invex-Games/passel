import 'dart:async';

import 'package:google_sign_in/google_sign_in.dart';

/// Encapsulates all Google Sign-In interactions.
///
/// The Riverpod [AuthService] delegates to this class, keeping the notifier
/// a thin UI-state adapter.  Accepts an optional [GoogleSignIn] instance for
/// testability.
class AuthRepository {
  AuthRepository({GoogleSignIn? googleSignIn})
      : _googleSignIn = googleSignIn ?? GoogleSignIn.instance;

  final GoogleSignIn _googleSignIn;
  String? _idToken;
  StreamSubscription<GoogleSignInAuthenticationEvent>? _authSub;

  /// Broadcast stream of sign-in state changes (`true` = signed in).
  ///
  /// Call [initialize] before listening.
  final _controller = StreamController<bool>.broadcast();

  Stream<bool> get authStateChanges => _controller.stream;

  /// Whether the user currently holds a valid ID token.
  bool get isSignedIn => _idToken != null;

  /// Current ID token, or `null` if not authenticated.
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
            _idToken = user.authentication.idToken;
            _controller.add(true);
          case GoogleSignInAuthenticationEventSignOut():
            _idToken = null;
            _controller.add(false);
        }
      },
      onError: (_) {
        _idToken = null;
        _controller.add(false);
      },
    );

    // Attempt silent sign-in to restore a previous session.
    try {
      final futureOrNull =
          _googleSignIn.attemptLightweightAuthentication();
      if (futureOrNull != null) {
        await futureOrNull;
      }
    } catch (_) {
      // Silent restore failed — stay signed out.
    }

    return _idToken != null;
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
      await _googleSignIn.authenticate(scopeHint: ['email', 'profile']);
      return true;
    } on GoogleSignInException catch (e) {
      if (e.code == GoogleSignInExceptionCode.canceled) {
        return false;
      }
      throw Exception('Google Sign-In failed (${e.code}): $e');
    }
  }

  /// Signs out and clears stored credentials.
  ///
  /// The packet cache is intentionally preserved so users can continue
  /// browsing previously-downloaded data while signed out.
  Future<void> signOut() async {
    try {
      await _googleSignIn.disconnect();
    } catch (_) {}
    _idToken = null;
    _controller.add(false);
  }

  /// Returns the current ID token, attempting a silent refresh if needed.
  Future<String?> getIdToken() async {
    if (_idToken != null) return _idToken;
    try {
      final futureOrNull = _googleSignIn.attemptLightweightAuthentication();
      if (futureOrNull != null) await futureOrNull;
    } catch (_) {}
    return _idToken;
  }

  /// Tears down subscriptions.  Call when the owner is disposed.
  void dispose() {
    _authSub?.cancel();
    _controller.close();
  }
}
