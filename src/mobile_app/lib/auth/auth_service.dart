import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'data/auth_repository.dart';
import '../config/app_config.dart';
import '../packets/data/packet_cache.dart';

// ─── Repository provider ─────────────────────────────────────────────────────

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  final repo = AuthRepository(
    // Per-user data must not outlive the session (sign-out) or leak to a
    // different account signing in on the same device.
    onUserDataInvalidated: PacketCache.clear,
  );
  ref.onDispose(repo.dispose);
  return repo;
});

// ─── Auth provider ───────────────────────────────────────────────────────────

final authServiceProvider =
    AsyncNotifierProvider<AuthService, bool>(AuthService.new);

// ─── Auth service (thin Riverpod adapter) ────────────────────────────────────

class AuthService extends AsyncNotifier<bool> {
  @override
  Future<bool> build() async {
    final repo = ref.read(authRepositoryProvider);

    // Forward repository auth-state changes into Riverpod state.
    final sub = repo.authStateChanges.listen((signedIn) {
      state = AsyncData(signedIn);
    });
    ref.onDispose(sub.cancel);

    return repo.initialize(
      clientId: AppConfig.appClientId.isEmpty ? null : AppConfig.appClientId,
      serverClientId: AppConfig.serverClientId,
    );
  }

  // ── Public API ──────────────────────────────────────────────────────────────

  /// Interactive sign-in. Returns `true` on success, `false` if cancelled.
  Future<bool> signIn() => ref.read(authRepositoryProvider).signIn();

  /// Signs out and clears the user's locally cached data.
  ///
  /// Providers holding per-user state (e.g. the packet feed) watch this
  /// provider and rebuild from the now-empty cache.
  Future<void> signOut() => ref.read(authRepositoryProvider).signOut();

  /// Returns a non-expired ID token to use as a Bearer token, refreshing it
  /// silently when needed.  Pass [forceRefresh] after an HTTP 401.
  ///
  /// Returns `null` (and the state flips to signed-out) if the session can't
  /// be refreshed.
  Future<String?> getIdToken({bool forceRefresh = false}) =>
      ref.read(authRepositoryProvider).getIdToken(forceRefresh: forceRefresh);
}
