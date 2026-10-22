import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Text shared into the app from other apps: the share sheet (`ACTION_SEND`
/// with `text/plain`) and the text selection menu (`ACTION_PROCESS_TEXT`).
///
/// `MainActivity` hands it over on the `passel/share` channel: text that
/// arrived before the app was ready waits for [takePendingText], text that
/// arrives while the app runs comes through [sharedText].
class ShareIntent {
  ShareIntent({MethodChannel? channel})
      : _channel = channel ?? const MethodChannel(channelName) {
    _channel.setMethodCallHandler(_onMethodCall);
  }

  static const String channelName = 'passel/share';

  final MethodChannel _channel;
  final _shared = StreamController<String>.broadcast();

  /// Text shared while the app runs, trimmed and never empty.
  Stream<String> get sharedText => _shared.stream;

  /// Text shared before the app was ready, trimmed, or `null` when there is
  /// none. Each piece of text is handed out once.
  Future<String?> takePendingText() async {
    try {
      return _clean(await _channel.invokeMethod<String>('takePendingText'));
    } on MissingPluginException {
      // Not Android, or a test without the channel.
      return null;
    } on PlatformException {
      return null;
    }
  }

  Future<Object?> _onMethodCall(MethodCall call) async {
    if (call.method == 'textShared') {
      final text = _clean(call.arguments as String?);
      if (text != null && !_shared.isClosed) _shared.add(text);
    }
    return null;
  }

  static String? _clean(String? text) {
    final trimmed = text?.trim();
    return trimmed == null || trimmed.isEmpty ? null : trimmed;
  }

  void dispose() {
    _channel.setMethodCallHandler(null);
    _shared.close();
  }
}

final shareIntentProvider = Provider<ShareIntent>((ref) {
  final shareIntent = ShareIntent();
  ref.onDispose(shareIntent.dispose);
  return shareIntent;
});
