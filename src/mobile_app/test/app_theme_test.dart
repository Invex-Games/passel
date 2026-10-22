import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/app.dart';
import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/settings/theme_mode_setting.dart';

import 'fakes.dart';

/// [MyApp] draws its pages in the theme chosen in Settings.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  /// Pumps the whole app.  Without an API URL defined, as in tests, it shows
  /// the configuration error page; the theme applies to it all the same.
  Future<ProviderContainer> pumpApp(WidgetTester tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService()),
        packetRepositoryProvider.overrideWithValue(FakePacketRepository()),
        appForegroundProvider.overrideWith(() => FakeAppForeground()),
        immediateCacheWrites(),
      ],
      child: const MyApp(),
    ));
    await tester.pumpAndSettle();
    return ProviderScope.containerOf(tester.element(find.byType(MyApp)));
  }

  /// The brightness of the theme the page is drawn with.
  Brightness brightness(WidgetTester tester) =>
      Theme.of(tester.element(find.byType(Scaffold).first)).brightness;

  testWidgets(
      'follows the device until a theme is chosen, then switches at '
      'once', (tester) async {
    final container = await pumpApp(tester);
    expect(brightness(tester), Brightness.light);

    await container.read(themeModeProvider.notifier).set(ThemeMode.dark);
    await tester.pumpAndSettle();
    expect(brightness(tester), Brightness.dark);

    await container.read(themeModeProvider.notifier).set(ThemeMode.light);
    await tester.pumpAndSettle();
    expect(brightness(tester), Brightness.light);
  });

  testWidgets('starts in the theme chosen last time', (tester) async {
    SharedPreferences.setMockInitialValues({ThemeModeSetting.key: 'dark'});

    await pumpApp(tester);

    expect(brightness(tester), Brightness.dark);
  });
}
