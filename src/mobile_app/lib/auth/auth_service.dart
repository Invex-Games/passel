import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'data/account_info.dart';
import 'data/auth_repository.dart';
import '../config/app_config.dart';
import '../packets/data/packet_cache.dart';
import '../provider_retry.dart';

// ─── Repository provider ─────────────────────────────────────────────────────

/// Bumped whenever the signed-in user's locally stored data is cleared: on
/// explicit sign-out and when a different account signs in.
///
/// Providers holding per-user state read or watch it so they drop what they
/// hold in memory (and don't write it back to disk) instead of re-reading
/// the cleared cache to find out.  It is bumped *before* the cache is
/// cleared and before the auth state flips, so by the time they rebuild the
/// signal is already there.
final userDataGenerationProvider =
    NotifierProvider<UserDataGeneration, int>(UserDataGeneration.new);

class UserDataGeneration extends Notifier<int> {
  @override
  int build() => 0;

  /// Records that the signed-in user's locally stored data was cleared.
  void bump() => state++;
}

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  final repo = AuthRepository(
    // A development build may sign in as a fixed user without Google.
    devUser: AppConfig.usesDevUser ? AppConfig.devUser : '',
    // Per-user data must not outlive the session (sign-out) or leak to a
    // different account signing in on the same device.
    onUserDataInvalidated: () async {
      ref.read(userDataGenerationProvider.notifier).bump();
      await PacketCache.clear();
    },
  );
  ref.onDispose(repo.dispose);
  return repo;
});

// ─── Auth provider ───────────────────────────────────────────────────────────

final authServiceProvider = AsyncNotifierProvider<AuthService, bool>(
  AuthService.new,
  retry: noRetry,
);

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
  ///
  /// [devUser] picks the development user in a build with `DEV_USER` set and
  /// is ignored otherwise; see [AuthRepository.signIn].
  Future<bool> signIn({String? devUser}) =>
      ref.read(authRepositoryProvider).signIn(devUser: devUser);

  /// The development user signed in right now, or `null`.
  String? get currentDevUser =>
      ref.read(authRepositoryProvider).currentDevUser;

  /// The Google account signed in right now, or `null` when signed out or
  /// signed in as a development user.
  AccountInfo? get currentAccount =>
      ref.read(authRepositoryProvider).currentAccount;

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
