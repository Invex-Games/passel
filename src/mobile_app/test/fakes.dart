import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';

// ─── Auth fakes ──────────────────────────────────────────────────────────────

/// Fake auth service that resolves immediately.
///
/// Pass [signedIn] to control whether [build] returns signed-in or signed-out.
class FakeAuthService extends AsyncNotifier<bool> implements AuthService {
  FakeAuthService({this.signedIn = false});
  final bool signedIn;

  @override
  Future<bool> build() async => signedIn;

  @override
  Future<bool> signIn() async {
    state = const AsyncData(true);
    return true;
  }

  @override
  Future<void> signOut() async {
    state = const AsyncData(false);
  }

  @override
  Future<String?> getIdToken() async => signedIn ? 'fake-token' : null;
}

/// Auth service whose [signIn] completes only when the caller completes the future.
class SlowAuthService extends AsyncNotifier<bool> implements AuthService {
  SlowAuthService(this._signInFuture);
  final Future<bool> _signInFuture;

  @override
  Future<bool> build() async => false;

  @override
  Future<bool> signIn() => _signInFuture;

  @override
  Future<void> signOut() async {}

  @override
  Future<String?> getIdToken() async => 'fake-token';
}
