import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/auth/data/account_info.dart';
import 'package:passel_mobileapp/settings/app_version.dart';
import 'package:passel_mobileapp/settings/settings_page.dart';
import 'package:passel_mobileapp/settings/theme_mode_setting.dart';

import '../fakes.dart';

/// The settings page: its Account, Appearance and About sections.
void main() {
  late FakeAuthService auth;

  const ada =
      AccountInfo(email: 'ada@example.com', displayName: 'Ada Lovelace');

  /// Pumps the page on a phone-sized screen, so every section is on it.
  Future<void> pumpSettings(
    WidgetTester tester, {
    bool signedIn = true,
    AccountInfo? account,
    String? devUser,
    Future<AppVersion> Function()? version,
  }) async {
    tester.view.physicalSize = const Size(1080, 2400);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    auth = FakeAuthService(
      signedIn: signedIn,
      account: account,
      devUser: devUser,
    );
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => auth),
        appVersionProvider.overrideWith(
          (ref) => version != null
              ? version()
              : Future.value(
                  const AppVersion(version: '1.2.0', buildNumber: '42'),
                ),
        ),
      ],
      child: const MaterialApp(home: SettingsPage()),
    ));
    await tester.pumpAndSettle();
  }

  /// The tile titled [title].
  ListTile tile(WidgetTester tester, String title) => tester.widget<ListTile>(
        find.ancestor(of: find.text(title), matching: find.byType(ListTile)),
      );

  setUp(() => SharedPreferences.setMockInitialValues({}));

  group('Account', () {
    testWidgets('names the Google account and offers Sign out', (tester) async {
      await pumpSettings(tester, account: ada);

      expect(find.text('Ada Lovelace'), findsOneWidget);
      expect(find.text('ada@example.com'), findsOneWidget);
      expect(find.text('A'), findsOneWidget, reason: 'the avatar initial');
      expect(find.text('Sign out'), findsOneWidget);
      expect(find.text('Sign in'), findsNothing);
    });

    testWidgets('falls back to the email when the account has no name',
        (tester) async {
      await pumpSettings(
        tester,
        account: const AccountInfo(email: 'ada@example.com'),
      );

      expect(find.text('ada@example.com'), findsOneWidget);
      expect(find.text('Google account'), findsOneWidget);
    });

    testWidgets('names the development user', (tester) async {
      await pumpSettings(tester, devUser: 'alice');

      expect(find.text('alice'), findsOneWidget);
      expect(find.text('Development user'), findsOneWidget);
      expect(find.text('Sign out'), findsOneWidget);
    });

    testWidgets('signing out asks the service and shows the signed-out state',
        (tester) async {
      await pumpSettings(tester, account: ada);

      await tester.tap(find.text('Sign out'));
      await tester.pumpAndSettle();

      expect(auth.signOutCalls, 1);
      expect(find.text('Ada Lovelace'), findsNothing);
      expect(find.text('Not signed in'), findsOneWidget);
      expect(find.text('Sign in'), findsOneWidget);
    });

    testWidgets(
        'signed out, Sign in opens the sign-in sheet, and signing in names '
        'the account', (tester) async {
      await pumpSettings(tester, signedIn: false, account: ada);

      expect(find.text('Not signed in'), findsOneWidget);
      expect(find.text('Sign out'), findsNothing);

      await tester.tap(find.text('Sign in'));
      await tester.pumpAndSettle();
      expect(find.text('Sign in to sync'), findsOneWidget);

      await tester.tap(find.text('Sign in with Google'));
      await tester.pumpAndSettle();

      expect(auth.signInCalls, 1);
      expect(find.text('Sign in to sync'), findsNothing);
      expect(find.text('Ada Lovelace'), findsOneWidget);
      expect(find.text('Sign out'), findsOneWidget);
    });
  });

  group('Appearance', () {
    SegmentedButton<ThemeMode> themeButton(WidgetTester tester) =>
        tester.widget(find.byType(SegmentedButton<ThemeMode>));

    testWidgets('offers the three themes, with the device default selected',
        (tester) async {
      await pumpSettings(tester);

      expect(find.text('Theme'), findsOneWidget);
      expect(find.text('System default'), findsOneWidget);
      expect(themeButton(tester).selected, {ThemeMode.system});
      expect(find.text('System'), findsOneWidget);
      expect(find.text('Light'), findsOneWidget);
      expect(find.text('Dark'), findsOneWidget);
    });

    testWidgets(
        'choosing a theme applies it at once and keeps it across a restart',
        (tester) async {
      await pumpSettings(tester);
      final container = ProviderScope.containerOf(
        tester.element(find.byType(SettingsPage)),
      );

      await tester.tap(find.text('Dark'));
      await tester.pumpAndSettle();

      expect(container.read(themeModeProvider), ThemeMode.dark);
      expect(themeButton(tester).selected, {ThemeMode.dark});
      // The segment and, now, the subtitle of the Theme tile.
      expect(find.text('Dark'), findsNWidgets(2));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString(ThemeModeSetting.key), 'dark');

      // A restart: a fresh scope reads the saved choice.
      await tester.pumpWidget(const SizedBox.shrink());
      await pumpSettings(tester);

      expect(themeButton(tester).selected, {ThemeMode.dark});
      expect(find.text('Dark'), findsNWidgets(2));
    });
  });

  group('About', () {
    testWidgets('shows the version with its build number', (tester) async {
      await pumpSettings(tester);

      expect(find.text('Version'), findsOneWidget);
      expect(find.text('1.2.0 (42)'), findsOneWidget);
    });

    testWidgets('says so when the platform has no version', (tester) async {
      await pumpSettings(
        tester,
        version: () => throw StateError('no platform'),
      );

      expect(find.text('Unknown'), findsOneWidget);
    });

    testWidgets('shows the flavor and the API in a development build',
        (tester) async {
      await pumpSettings(tester);

      // Tests run as the development flavor, without an API URL defined.
      expect(find.text('Flavor'), findsOneWidget);
      expect(find.text('development'), findsOneWidget);
      expect(find.text('API'), findsOneWidget);
      expect(find.text('Not set'), findsOneWidget);
    });

    testWidgets('lists the privacy policy and terms, disabled until published',
        (tester) async {
      await pumpSettings(tester);

      for (final title in ['Privacy policy', 'Terms of service']) {
        expect(tile(tester, title).enabled, legalPagesPublished);
      }
      expect(
        find.text('Not published yet'),
        findsNWidgets(legalPagesPublished ? 0 : 2),
      );
    });
  });
}
