import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/api/errors.dart';
import 'package:passel_mobileapp/auth/data/auth_repository.dart';
import 'package:passel_mobileapp/auth/data/authorization.dart';

import '../fakes.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('AuthRepository with a development user', () {
    late FakeGoogleSignIn google;
    late int invalidations;
    late List<bool> authEvents;

    setUp(() {
      SharedPreferences.setMockInitialValues({});
      google = FakeGoogleSignIn()
        ..onInitialize =
            () async => throw StateError('Google must not be initialised');
      invalidations = 0;
      authEvents = [];
    });

    AuthRepository buildRepo() {
      final repo = AuthRepository(
        googleSignIn: google,
        devUser: 'alice',
        onUserDataInvalidated: () async => invalidations++,
      );
      final sub = repo.authStateChanges.listen(authEvents.add);
      addTearDown(() {
        sub.cancel();
        repo.dispose();
      });
      return repo;
    }

    Future<void> settle() => Future<void>.delayed(Duration.zero);

    Future<String?> remembered(String key) async =>
        (await SharedPreferences.getInstance()).getString(key);

    test('initialize signs in as the default user without touching Google',
        () async {
      final repo = buildRepo();

      expect(await repo.initialize(clientId: null, serverClientId: ''), isTrue);
      await settle();

      expect(repo.isSignedIn, isTrue);
      expect(repo.currentDevUser, 'alice');
      expect(await repo.getIdToken(), devToken('alice'));
      expect(authorizationHeader((await repo.getIdToken())!), 'Dev alice');
      expect(google.lightweightCalls, 0);
      expect(authEvents, [true]);
      expect(await remembered(AuthRepository.devUserKey), 'alice');
      expect(await remembered(AuthRepository.lastUserIdKey), 'dev:alice');
    });

    test('a forced refresh keeps the development token', () async {
      final repo = buildRepo();
      await repo.initialize(clientId: null, serverClientId: '');

      expect(await repo.getIdToken(forceRefresh: true), devToken('alice'));
      expect(repo.isSignedIn, isTrue);
    });

    test('signing in as another user is an account change', () async {
      final repo = buildRepo();
      await repo.initialize(clientId: null, serverClientId: '');

      expect(await repo.signIn(devUser: ' bob '), isTrue);
      await settle();

      expect(repo.currentDevUser, 'bob');
      expect(await repo.getIdToken(), devToken('bob'));
      expect(invalidations, 1, reason: 'the previous user\'s data is cleared');
      expect(authEvents, [true, false, true]);
      expect(await remembered(AuthRepository.devUserKey), 'bob');
      expect(await remembered(AuthRepository.lastUserIdKey), 'dev:bob');
    });

    test('signing in as the same user again changes nothing', () async {
      final repo = buildRepo();
      await repo.initialize(clientId: null, serverClientId: '');

      expect(await repo.signIn(devUser: 'alice'), isTrue);
      await settle();

      expect(invalidations, 0);
      expect(authEvents, [true, true]);
    });

    test('refuses a user the API would refuse', () async {
      final repo = buildRepo();
      await repo.initialize(clientId: null, serverClientId: '');

      await expectLater(
        repo.signIn(devUser: 'not valid!'),
        throwsA(isA<SignInException>()
            .having((e) => e.message, 'message', contains('1 to 255'))),
      );
      expect(repo.currentDevUser, 'alice');
    });

    test('a restart signs in as the user last used', () async {
      SharedPreferences.setMockInitialValues({
        AuthRepository.devUserKey: 'carol',
        AuthRepository.lastUserIdKey: 'dev:carol',
      });
      final repo = buildRepo();

      await repo.initialize(clientId: null, serverClientId: '');

      expect(repo.currentDevUser, 'carol');
      expect(invalidations, 0);
    });

    test('a different development user than last time clears cached data',
        () async {
      SharedPreferences.setMockInitialValues(
          {AuthRepository.lastUserIdKey: 'dev:bob'});
      final repo = buildRepo();

      await repo.initialize(clientId: null, serverClientId: '');

      expect(invalidations, 1);
      expect(await remembered(AuthRepository.lastUserIdKey), 'dev:alice');
    });

    test('signing out forgets the user and clears data; signing in again '
        'needs no Google', () async {
      final repo = buildRepo();
      await repo.initialize(clientId: null, serverClientId: '');

      await repo.signOut();
      await settle();
      expect(repo.isSignedIn, isFalse);
      expect(repo.currentDevUser, isNull);
      expect(invalidations, 1);
      expect(google.signOutCalls, 0);
      expect(await remembered(AuthRepository.devUserKey), isNull);

      expect(await repo.signIn(), isTrue);
      await settle();
      expect(repo.currentDevUser, 'alice');
      expect(authEvents, [true, false, true]);
    });
  });
}
