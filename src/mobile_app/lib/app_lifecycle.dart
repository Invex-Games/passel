import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

// ─── App lifecycle provider ──────────────────────────────────────────────────

/// Whether the app is in the foreground (visible, even if not focused).
///
/// Live connections are only kept open while this is `true`: the OS suspends
/// background apps, so their connections would drop anyway.
final appForegroundProvider =
    NotifierProvider<AppForegroundNotifier, bool>(AppForegroundNotifier.new);

class AppForegroundNotifier extends Notifier<bool> {
  @override
  bool build() {
    final listener = AppLifecycleListener(
      onStateChange: (lifecycleState) => state = _isForeground(lifecycleState),
    );
    ref.onDispose(listener.dispose);
    return _isForeground(WidgetsBinding.instance.lifecycleState);
  }

  /// The state is `null` until the first lifecycle event, i.e. while the app
  /// is starting up in the foreground.
  static bool _isForeground(AppLifecycleState? lifecycleState) =>
      switch (lifecycleState) {
        null || AppLifecycleState.resumed || AppLifecycleState.inactive =>
          true,
        _ => false,
      };
}
