import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/share/share_intent.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const channel = MethodChannel(ShareIntent.channelName);
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  /// Answers the platform side's `takePendingText` with [pending].
  void platformHas(String? pending) {
    messenger.setMockMethodCallHandler(channel, (call) async {
      expect(call.method, 'takePendingText');
      return pending;
    });
  }

  /// Delivers a `textShared` call as the platform side would.
  Future<void> platformShares(String? text) => messenger.handlePlatformMessage(
        ShareIntent.channelName,
        const StandardMethodCodec()
            .encodeMethodCall(MethodCall('textShared', text)),
        (_) {},
      );

  tearDown(() => messenger.setMockMethodCallHandler(channel, null));

  test('hands out pending text once, trimmed', () async {
    platformHas('  https://example.com  ');
    final shareIntent = ShareIntent();
    addTearDown(shareIntent.dispose);

    expect(await shareIntent.takePendingText(), 'https://example.com');
  });

  test('blank pending text is nothing', () async {
    platformHas('   ');
    final shareIntent = ShareIntent();
    addTearDown(shareIntent.dispose);

    expect(await shareIntent.takePendingText(), isNull);
  });

  test('no platform side means nothing pending', () async {
    final shareIntent = ShareIntent();
    addTearDown(shareIntent.dispose);

    expect(await shareIntent.takePendingText(), isNull);
  });

  test('text shared while running is streamed, trimmed, never blank',
      () async {
    final shareIntent = ShareIntent();
    addTearDown(shareIntent.dispose);
    final received = <String>[];
    final sub = shareIntent.sharedText.listen(received.add);
    addTearDown(sub.cancel);

    await platformShares(' a note ');
    await platformShares('   ');
    await platformShares(null);
    await platformShares('https://example.com');

    expect(received, ['a note', 'https://example.com']);
  });
}
