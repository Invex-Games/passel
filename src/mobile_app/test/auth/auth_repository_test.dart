import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:google_sign_in/google_sign_in.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/auth/data/auth_repository.dart';
import 'package:passel_mobileapp/auth/data/jwt.dart';

import '../fakes.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final now = DateTime.utc(2026, 10, 21, 12);

  // ── JWT decoding ───────────────────────────────────────────────────────────

  group('jwtExpiry', () {
    test('reads the exp claim', () {
      final exp = DateTime.utc(2026, 10, 21, 13);
      expect(jwtExpiry(fakeJwt(exp)), exp);
    });

    test('returns null for malformed tokens', () {
      expect(jwtExpiry(''), isNull);
      expect(jwtExpiry('not-a-jwt'), isNull);
      expect(jwtExpiry('a.b'), isNull);
      expect(jwtExpiry('a.b.c.d'), isNull);
      expect(jwtExpiry('header.%%%not-base64%%%.sig'), isNull);
      // Valid base64, but not JSON.
      expect(jwtExpiry('h.aGVsbG8.s'), isNull);
      // JSON, but not an object ("[1]").
      expect(jwtExpiry('h.WzFd.s'), isNull);
    });

    test('returns null when exp is missing or not a number', () {
      final noExp = fakeJwt(now).split('.');
      // {"sub":"x"}
      noExp[1] = 'eyJzdWIiOiJ4In0';
      expect(jwtExpiry(noExp.join('.')), isNull);

      // {"exp":"soon"}
      expect(jwtExpiry('h.eyJleHAiOiJzb29uIn0.s'), isNull);
    });
  });

  group('isTokenExpired', () {
    test('is false well before expiry', () {
      final token = fakeJwt(now.add(const Duration(hours: 1)));
      expect(isTokenExpired(token, now: now), isFalse);
    });

    test('is true within the leeway window', () {
      final token = fakeJwt(now.add(const Duration(minutes: 4)));
      expect(isTokenExpired(token, now: now), isTrue);
      expect(
        isTokenExpired(token, now: now, leeway: Duration.zero),
        isFalse,
      );
    });

    test('is true after expiry', () {
      final token = fakeJwt(now.subtract(const Duration(seconds: 1)));
      expect(isTokenExpired(token, now: now, leeway: Duration.zero), isTrue);
    });

    test('treats malformed tokens as expired', () {
      expect(isTokenExpired('garbage', now: now), isTrue);
    });

    test('handles non-UTC "now"', () {
      final token = fakeJwt(now.add(const Duration(hours: 1)));
      expect(isTokenExpired(token, now: now.toLocal()), isFalse);
    });
  });

  // ── AuthRepository ─────────────────────────────────────────────────────────

  group('AuthRepository', () {
    late FakeGoogleSignIn google;
    late DateTime clock;
    late int invalidations;
    late List<bool> authEvents;

    setUp(() {
      SharedPreferences.setMockInitialValues({});
      google = FakeGoogleSignIn();
      clock = now;
      invalidations = 0;
      authEvents = [];
    });

    AuthRepository buildRepo() {
      final repo = AuthRepository(
        googleSignIn: google,
        clock: () => clock,
        onUserDataInvalidated: () async => invalidations++,
      );
      final sub = repo.authStateChanges.listen(authEvents.add);
      addTearDown(() {
        sub.cancel();
        repo.dispose();
      });
      return repo;
    }

    Future<void> init(AuthRepository repo) =>
        repo.initialize(clientId: null, serverClientId: 'server');

    FakeGoogleAccount account(String id, Duration validFor) =>
        FakeGoogleAccount(id: id, idToken: fakeJwt(clock.add(validFor)));

    test('initialize restores a previous session silently', () async {
      final restored = account('user-1', const Duration(hours: 1));
      google.onLightweight = () async => restored;
      final repo = buildRepo();

      await init(repo);

      expect(repo.isSignedIn, isTrue);
      expect(repo.idToken, restored.idToken);
    });

    test('returns the cached token while it is not near expiry', () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);
      final callsAfterInit = google.lightweightCalls;

      final token = await repo.getIdToken();

      expect(token, isNotNull);
      expect(google.lightweightCalls, callsAfterInit);
    });

    test('refreshes silently when the token expires within 5 minutes',
        () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);
      final original = repo.idToken;

      // Move to 2 minutes before expiry; next silent sign-in yields a new
      // token valid for another hour.
      clock = clock.add(const Duration(minutes: 58));
      final fresh = account('user-1', const Duration(hours: 1));
      google.onLightweight = () async => fresh;

      final token = await repo.getIdToken();

      expect(token, fresh.idToken);
      expect(token, isNot(original));
      expect(repo.isSignedIn, isTrue);
    });

    test('signs out when the token expired and refresh fails', () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);
      await pumpEventQueue();
      authEvents.clear();

      clock = clock.add(const Duration(hours: 2));
      google.onLightweight = () async => null;

      final token = await repo.getIdToken();
      await pumpEventQueue();

      expect(token, isNull);
      expect(repo.isSignedIn, isFalse);
      expect(authEvents, [false]);
      // An expired session keeps the cached data (no explicit sign-out).
      expect(invalidations, 0);
    });

    test('signs out when silent refresh throws', () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);

      clock = clock.add(const Duration(hours: 2));
      google.onLightweight = () async =>
          throw const GoogleSignInException(
              code: GoogleSignInExceptionCode.unknownError);

      expect(await repo.getIdToken(), isNull);
      expect(repo.isSignedIn, isFalse);
    });

    test('keeps a near-expiry but still valid token if refresh fails',
        () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);
      final original = repo.idToken;

      clock = clock.add(const Duration(minutes: 57)); // 3 min left
      google.onLightweight = () async => null;

      expect(await repo.getIdToken(), original);
      expect(repo.isSignedIn, isTrue);
    });

    test('forceRefresh replaces a token the server rejected', () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);
      final rejected = repo.idToken;

      final fresh = account('user-1', const Duration(hours: 2));
      google.onLightweight = () async => fresh;

      final token = await repo.getIdToken(forceRefresh: true);

      expect(token, fresh.idToken);
      expect(token, isNot(rejected));
    });

    test('forceRefresh signs out if only the rejected token comes back',
        () async {
      final original = account('user-1', const Duration(hours: 1));
      google.onLightweight = () async => original;
      final repo = buildRepo();
      await init(repo);

      final token = await repo.getIdToken(forceRefresh: true);

      expect(token, isNull);
      expect(repo.isSignedIn, isFalse);
    });

    test('concurrent callers share a single refresh', () async {
      google.onLightweight =
          () async => account('user-1', const Duration(minutes: 1));
      final repo = buildRepo();
      await init(repo);
      final callsAfterInit = google.lightweightCalls;

      final gate = Completer<GoogleSignInAccount?>();
      google.onLightweight = () => gate.future;

      final a = repo.getIdToken();
      final b = repo.getIdToken();
      gate.complete(account('user-1', const Duration(hours: 1)));

      final tokens = await Future.wait([a, b]);
      expect(tokens[0], isNotNull);
      expect(tokens[0], tokens[1]);
      expect(google.lightweightCalls, callsAfterInit + 1);
    });

    test('signOut uses signOut (not disconnect) and clears user data',
        () async {
      google.onLightweight =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);

      await repo.signOut();
      await pumpEventQueue();

      expect(google.signOutCalls, 1);
      expect(google.disconnectCalls, 0);
      expect(invalidations, 1);
      expect(repo.isSignedIn, isFalse);
      expect(authEvents.last, isFalse);

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString(AuthRepository.lastUserIdKey), isNull);
    });

    test('clears user data when a different account signs in', () async {
      SharedPreferences.setMockInitialValues(
          {AuthRepository.lastUserIdKey: 'user-1'});
      google.onAuthenticate =
          () async => account('user-2', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);

      await repo.signIn();

      expect(invalidations, 1);
      expect(repo.isSignedIn, isTrue);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString(AuthRepository.lastUserIdKey), 'user-2');
    });

    test('keeps user data when the same account signs back in', () async {
      SharedPreferences.setMockInitialValues(
          {AuthRepository.lastUserIdKey: 'user-1'});
      google.onAuthenticate =
          () async => account('user-1', const Duration(hours: 1));
      final repo = buildRepo();
      await init(repo);

      await repo.signIn();

      expect(invalidations, 0);
      expect(repo.isSignedIn, isTrue);
    });

    test('signIn returns false when the user cancels', () async {
      google.onAuthenticate = () async => throw const GoogleSignInException(
          code: GoogleSignInExceptionCode.canceled);
      final repo = buildRepo();
      await init(repo);

      expect(await repo.signIn(), isFalse);
      expect(repo.isSignedIn, isFalse);
    });
  });
}
