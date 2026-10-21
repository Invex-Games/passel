import 'dart:async';
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:google_sign_in/google_sign_in.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_repository.dart';

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
  Future<String?> getIdToken({bool forceRefresh = false}) async =>
      signedIn ? 'fake-token' : null;
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
  Future<String?> getIdToken({bool forceRefresh = false}) async => 'fake-token';
}

// ─── Google Sign-In fakes ────────────────────────────────────────────────────

/// Builds an unsigned JWT whose payload carries [exp] (and [extraClaims]).
String fakeJwt(DateTime exp, {Map<String, Object?> extraClaims = const {}}) {
  String encode(Map<String, Object?> json) =>
      base64Url.encode(utf8.encode(jsonEncode(json))).replaceAll('=', '');
  final header = encode({'alg': 'none', 'typ': 'JWT'});
  final payload = encode({
    'exp': exp.millisecondsSinceEpoch ~/ 1000,
    ...extraClaims,
  });
  return '$header.$payload.signature';
}

/// Minimal [GoogleSignInAccount] carrying an [id] and [idToken].
class FakeGoogleAccount implements GoogleSignInAccount {
  FakeGoogleAccount({required this.id, required this.idToken});

  @override
  final String id;

  final String? idToken;

  @override
  String get email => '$id@example.com';

  @override
  String? get displayName => null;

  @override
  String? get photoUrl => null;

  @override
  GoogleSignInAuthentication get authentication =>
      GoogleSignInAuthentication(idToken: idToken);

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      super.noSuchMethod(invocation);
}

/// Scriptable [GoogleSignIn] replacement.
///
/// Set [onLightweight] to decide what each silent sign-in attempt returns,
/// and [onAuthenticate] for interactive sign-in.
class FakeGoogleSignIn implements GoogleSignIn {
  final _events =
      StreamController<GoogleSignInAuthenticationEvent>.broadcast();

  Future<GoogleSignInAccount?> Function() onLightweight =
      () async => null;
  Future<GoogleSignInAccount> Function()? onAuthenticate;

  int lightweightCalls = 0;
  int signOutCalls = 0;
  int disconnectCalls = 0;

  @override
  Future<void> initialize({
    String? clientId,
    String? serverClientId,
    String? nonce,
    String? hostedDomain,
  }) async {}

  @override
  Stream<GoogleSignInAuthenticationEvent> get authenticationEvents =>
      _events.stream;

  @override
  Future<GoogleSignInAccount?>? attemptLightweightAuthentication({
    bool reportAllExceptions = false,
  }) {
    lightweightCalls++;
    return onLightweight();
  }

  @override
  bool supportsAuthenticate() => true;

  @override
  Future<GoogleSignInAccount> authenticate({
    List<String> scopeHint = const <String>[],
  }) =>
      onAuthenticate!();

  @override
  Future<void> signOut() async {
    signOutCalls++;
  }

  @override
  Future<void> disconnect() async {
    disconnectCalls++;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      super.noSuchMethod(invocation);
}

// ─── Packet fakes ────────────────────────────────────────────────────────────

/// In-memory cache that avoids SharedPreferences / Flutter bindings.
class InMemoryPacketCache implements PacketCacheStrategy {
  InMemoryPacketCache([this.state]);

  PacketsState? state;

  @override
  Future<PacketsState?> load() async => state;

  @override
  Future<void> save(PacketsState state) async => this.state = state;

  @override
  Future<void> clear() async => state = null;
}

/// [PacketRepository] whose network calls are scripted per test.
class FakePacketRepository extends PacketRepository {
  FakePacketRepository({InMemoryPacketCache? cache})
      : this._(cache ?? InMemoryPacketCache());

  FakePacketRepository._(this.cache)
      : super(
          client: MockClient((_) async => http.Response('unexpected', 500)),
          tokenProvider: ({bool forceRefresh = false}) async => 'fake-token',
          cache: cache,
          baseUrl: 'https://test.example.com',
        );

  final InMemoryPacketCache cache;

  /// Handles [fetchPackets]; `cursor` is `null` for the first page.
  Future<PacketsState> Function(String? cursor) onFetch =
      (_) async => const PacketsState();

  /// Handles [pushPacket].
  Future<Packet?> Function(PacketType type, String payload) onPush =
      (_, __) async => null;

  /// Cursors passed to [fetchPackets], in call order.
  final fetchCursors = <String?>[];

  @override
  Future<PacketsState> fetchPackets({String? cursor}) {
    fetchCursors.add(cursor);
    return onFetch(cursor);
  }

  @override
  Future<Packet?> pushPacket({
    required PacketType type,
    required String payload,
    String? title,
  }) =>
      onPush(type, payload);

  /// Live connections opened via [watchEvents], oldest first.  The test
  /// drives each one by adding events / errors or closing it.
  final connections = <StreamController<PacketEvent>>[];

  @override
  Stream<PacketEvent> watchEvents() {
    final connection = StreamController<PacketEvent>();
    connections.add(connection);
    return connection.stream;
  }
}

// ─── Lifecycle fakes ─────────────────────────────────────────────────────────

/// [AppForegroundNotifier] that doesn't need a Flutter binding; flip it with
/// [setForeground].
class FakeAppForeground extends AppForegroundNotifier {
  FakeAppForeground({this.initial = true});
  final bool initial;

  @override
  bool build() => initial;

  void setForeground(bool foreground) => state = foreground;
}

/// Shorthand for a text packet with the given [id].
Packet textPacket(String id) =>
    Packet(id: id, type: PacketType.text, payload: 'body $id');
