import 'package:flutter/material.dart' show ThemeMode;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/settings/theme_mode_setting.dart';

/// The theme choice: read at start, applied at once, and remembered.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const key = ThemeModeSetting.key;

  /// A container for one run of the app; a second one is a restart.
  ProviderContainer run() {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    return container;
  }

  Future<String?> saved() async =>
      (await SharedPreferences.getInstance()).getString(key);

  setUp(() => SharedPreferences.setMockInitialValues({}));

  test('follows the device until a choice is made', () async {
    final app = run();

    expect(app.read(themeModeProvider), ThemeMode.system);
    await pumpEventQueue();

    expect(app.read(themeModeProvider), ThemeMode.system);
    expect(await saved(), isNull);
  });

  test('a choice applies at once and is kept for the next run', () async {
    final app = run();
    final changes = <ThemeMode>[];
    app.listen(themeModeProvider, (_, next) => changes.add(next));

    await app.read(themeModeProvider.notifier).set(ThemeMode.dark);

    expect(changes, [ThemeMode.dark]);
    expect(await saved(), 'dark');

    final nextRun = run();
    expect(nextRun.read(themeModeProvider), ThemeMode.system,
        reason: 'the saved choice takes a moment to read');
    await pumpEventQueue();
    expect(nextRun.read(themeModeProvider), ThemeMode.dark);
  });

  test('a choice made while the saved one is still being read wins', () async {
    SharedPreferences.setMockInitialValues({key: 'dark'});
    final app = run();

    expect(app.read(themeModeProvider), ThemeMode.system);
    await app.read(themeModeProvider.notifier).set(ThemeMode.light);
    await pumpEventQueue();

    expect(app.read(themeModeProvider), ThemeMode.light);
    expect(await saved(), 'light');
  });

  test('ignores a saved value it does not know', () async {
    SharedPreferences.setMockInitialValues({key: 'sepia'});
    final app = run();

    await pumpEventQueue();

    expect(app.read(themeModeProvider), ThemeMode.system);
  });

  test('parseThemeMode reads the names that are written', () {
    for (final mode in ThemeMode.values) {
      expect(parseThemeMode(mode.name), mode);
    }
    expect(parseThemeMode(null), isNull);
    expect(parseThemeMode('Dark'), isNull);
  });
}
