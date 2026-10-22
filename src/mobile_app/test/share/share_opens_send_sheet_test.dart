import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_feed_page.dart';
import 'package:passel_mobileapp/share/share_intent.dart';

import '../fakes.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const channel = MethodChannel(ShareIntent.channelName);
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  late FakePacketRepository repo;

  Future<void> pumpFeed(WidgetTester tester, {String? pending}) async {
    messenger.setMockMethodCallHandler(channel, (_) async => pending);
    repo = FakePacketRepository();
    repo.onFetch = (_) async => PacketsState(packets: [textPacket('a')]);
    repo.onPush = (type, payload) async =>
        Packet(id: 'new', type: type, payload: payload);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
        appForegroundProvider.overrideWith(() => FakeAppForeground()),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: PacketFeedPage()),
    ));
    await tester.pumpAndSettle();
  }

  Future<void> platformShares(String text) => messenger.handlePlatformMessage(
        ShareIntent.channelName,
        const StandardMethodCodec()
            .encodeMethodCall(MethodCall('textShared', text)),
        (_) {},
      );

  tearDown(() => messenger.setMockMethodCallHandler(channel, null));

  testWidgets('text shared before the app was ready opens the send sheet',
      (tester) async {
    await pumpFeed(tester, pending: 'https://example.com/shared');

    expect(find.text('Send a link or note'), findsOneWidget);
    expect(find.text('https://example.com/shared'), findsOneWidget);

    await tester.tap(find.text('Send'));
    await tester.pumpAndSettle();

    expect(find.text('Send a link or note'), findsNothing);
    expect(find.text('https://example.com/shared'), findsOneWidget);
  });

  testWidgets('text shared while the feed is shown opens the send sheet',
      (tester) async {
    await pumpFeed(tester);
    expect(find.text('Send a link or note'), findsNothing);

    await platformShares('a note from another app');
    await tester.pumpAndSettle();

    expect(find.text('Send a link or note'), findsOneWidget);
    expect(find.text('a note from another app'), findsOneWidget);
  });

  testWidgets('a second share while the sheet is open does not stack sheets',
      (tester) async {
    await pumpFeed(tester);

    await platformShares('first');
    await tester.pumpAndSettle();
    await platformShares('second');
    await tester.pumpAndSettle();

    expect(find.text('Send a link or note'), findsOneWidget);
    expect(find.text('first'), findsOneWidget);
    expect(find.text('second'), findsNothing);
  });
}
