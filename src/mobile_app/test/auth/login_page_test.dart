import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/auth/login_page.dart';

import '../fakes.dart';

void main() {
  group('LoginPage', () {
    testWidgets('renders sign-in button and branding', (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authServiceProvider.overrideWith(() => FakeAuthService()),
          ],
          child: const MaterialApp(home: LoginPage()),
        ),
      );

      expect(find.text('Passel'), findsOneWidget);
      expect(find.text('Sign in to continue'), findsOneWidget);
      expect(find.text('Sign in with Google'), findsOneWidget);
      expect(find.byIcon(Icons.login), findsOneWidget);
    });

    testWidgets('shows loading state while signing in', (tester) async {
      final completer = Completer<bool>();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authServiceProvider
                .overrideWith(() => SlowAuthService(completer.future)),
          ],
          child: const MaterialApp(home: LoginPage()),
        ),
      );

      // Tap but don't settle – check intermediate loading state.
      await tester.tap(find.text('Sign in with Google'));
      await tester.pump();

      expect(find.text('Signing in…'), findsOneWidget);

      // Complete the future so the test can clean up.
      completer.complete(true);
      await tester.pumpAndSettle();
    });
  });

  group('LoginSheet (via showLoginSheet)', () {
    testWidgets('renders sign-in button and maybe-later dismiss',
        (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authServiceProvider.overrideWith(() => FakeAuthService()),
          ],
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: ElevatedButton(
                  onPressed: () => showLoginSheet(context),
                  child: const Text('Open sheet'),
                ),
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Open sheet'));
      await tester.pumpAndSettle();

      expect(find.text('Sign in to sync'), findsOneWidget);
      expect(find.text('Sign in with Google'), findsOneWidget);
      expect(find.text('Maybe later'), findsOneWidget);
      expect(find.byIcon(Icons.lock_outline), findsOneWidget);
    });

    testWidgets('"Maybe later" dismisses the sheet', (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authServiceProvider.overrideWith(() => FakeAuthService()),
          ],
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: ElevatedButton(
                  onPressed: () => showLoginSheet(context),
                  child: const Text('Open sheet'),
                ),
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Open sheet'));
      await tester.pumpAndSettle();
      expect(find.text('Sign in to sync'), findsOneWidget);

      await tester.tap(find.text('Maybe later'));
      await tester.pumpAndSettle();
      expect(find.text('Sign in to sync'), findsNothing);
    });

    testWidgets('auto-dismisses when sign-in succeeds', (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authServiceProvider.overrideWith(() => FakeAuthService()),
          ],
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: ElevatedButton(
                  onPressed: () => showLoginSheet(context),
                  child: const Text('Open sheet'),
                ),
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Open sheet'));
      await tester.pumpAndSettle();
      expect(find.text('Sign in to sync'), findsOneWidget);

      // Tap sign-in — FakeAuthService.signIn() sets state to true.
      await tester.tap(find.text('Sign in with Google'));
      await tester.pumpAndSettle();

      // Sheet should be auto-dismissed.
      expect(find.text('Sign in to sync'), findsNothing);
    });
  });
}
