import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'data/auth_repository.dart';
import '../config/app_config.dart';

// ─── Repository provider ─────────────────────────────────────────────────────

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  final repo = AuthRepository();
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
      clientId: AppConfig.appClientId,
      serverClientId: AppConfig.serverClientId,
    );
  }

  // ── Public API ──────────────────────────────────────────────────────────────

  /// Interactive sign-in. Returns `true` on success, `false` if cancelled.
  Future<bool> signIn() => ref.read(authRepositoryProvider).signIn();

  /// Signs out and clears all stored credentials.
  Future<void> signOut() => ref.read(authRepositoryProvider).signOut();

  /// Returns the current ID token to use as a Bearer token.
  Future<String?> getIdToken() => ref.read(authRepositoryProvider).getIdToken();
}
