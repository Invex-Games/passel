import 'dart:async';

import 'package:flutter/material.dart' show ThemeMode;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// The theme chosen in Settings: the same as the device, or always light or
/// always dark.
///
/// `MyApp` applies it as the app's [ThemeMode], on top of the device's
/// dynamic colours, and the settings page shows and changes it.  The choice
/// is kept in shared_preferences so it survives a restart: the provider
/// starts out following the device and switches to the saved choice as soon
/// as it has been read, which on a cold start is before the first page shows
/// (the start-up router waits on the same preferences file for the feed
/// cache).
final themeModeProvider =
    NotifierProvider<ThemeModeSetting, ThemeMode>(ThemeModeSetting.new);

class ThemeModeSetting extends Notifier<ThemeMode> {
  /// SharedPreferences key holding the chosen mode by its [ThemeMode.name].
  static const String key = 'settings_theme_mode';

  /// Whether a choice was made since this notifier was built, so a read of
  /// the saved choice that lands later cannot undo it.
  bool _chosen = false;

  @override
  ThemeMode build() {
    _chosen = false;
    unawaited(_restore());
    return ThemeMode.system;
  }

  /// Applies [mode] right away and remembers it for the next start.
  Future<void> set(ThemeMode mode) async {
    _chosen = true;
    state = mode;
    await _write(mode);
  }

  Future<void> _restore() async {
    final saved = parseThemeMode(await _read());
    if (saved == null || _chosen || !ref.mounted) return;
    state = saved;
  }

  Future<String?> _read() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      return prefs.getString(key);
    } catch (_) {
      return null;
    }
  }

  Future<void> _write(ThemeMode mode) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString(key, mode.name);
    } catch (_) {
      // The choice still applies until the app is closed.
    }
  }
}

/// The [ThemeMode] saved under its [ThemeMode.name], or `null` for anything
/// else: nothing saved, or a value from a build that knew other modes.
ThemeMode? parseThemeMode(String? name) {
  for (final mode in ThemeMode.values) {
    if (mode.name == name) return mode;
  }
  return null;
}

/// What the settings page calls [mode].
String describeThemeMode(ThemeMode mode) => switch (mode) {
      ThemeMode.system => 'System default',
      ThemeMode.light => 'Light',
      ThemeMode.dark => 'Dark',
    };
