import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_feed_page.dart';

import '../fakes.dart';

/// The sheet opened from the feed's FAB, from typing to what reaches the
/// server and the feed.
void main() {
  late FakePacketRepository repo;

  /// What was sent, as type and payload, in order.
  late List<(PacketType, String)> sent;

  /// Pumps the feed, signed in, with one packet.
  Future<void> pumpFeed(WidgetTester tester) async {
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

  /// The sheet's text field; it is only in the tree while the sheet is open.
  final field = find.byType(TextField);

  /// The sheet's send button (the FAB's "Send" is a tooltip, not a label).
  final sendButton = find.text('Send');

  /// Opens the sheet from the FAB.
  Future<void> openSheet(WidgetTester tester) async {
    await tester.tap(find.byTooltip('Send'));
    await tester.pumpAndSettle();
    expect(find.text('Send a link or note'), findsOneWidget);
    expect(field, findsOneWidget);
  }

  /// Opens the sheet, types [text] and taps send.
  Future<void> send(WidgetTester tester, String text) async {
    await openSheet(tester);
    await tester.enterText(field, text);
    await tester.tap(sendButton);
    await tester.pumpAndSettle();
  }

  setUp(() {
    sent = [];
    repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(packets: [textPacket('a')]);
    repo.onPush = (type, payload) async {
      sent.add((type, payload));
      return Packet(id: 'new', type: type, payload: payload);
    };
  });

  testWidgets('sends nothing for empty or blank text and stays open',
      (tester) async {
    await pumpFeed(tester);
    await openSheet(tester);

    await tester.tap(sendButton);
    await tester.pumpAndSettle();
    expect(field, findsOneWidget);
    expect(sent, isEmpty);

    await tester.enterText(field, '  \n ');
    await tester.tap(sendButton);
    await tester.pumpAndSettle();
    expect(field, findsOneWidget);
    expect(sent, isEmpty);
  });

  testWidgets('sends a web address as a link and shows it in the feed',
      (tester) async {
    const url = 'https://example.com/page?x=1';
    await pumpFeed(tester);

    await send(tester, url);

    expect(sent, [(PacketType.link, url)]);
    expect(field, findsNothing);
    expect(find.text(url), findsOneWidget);
    expect(find.text('body a'), findsOneWidget);
    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('sends anything else as a note, trimmed', (tester) async {
    await pumpFeed(tester);

    await send(tester, '  hello world \n');

    expect(sent, [(PacketType.text, 'hello world')]);
    expect(field, findsNothing);
    expect(find.text('hello world'), findsOneWidget);
  });

  testWidgets('the keyboard send action sends too', (tester) async {
    await pumpFeed(tester);
    await openSheet(tester);

    await tester.enterText(field, 'from the keyboard');
    await tester.testTextInput.receiveAction(TextInputAction.send);
    await tester.pumpAndSettle();

    expect(sent, [(PacketType.text, 'from the keyboard')]);
    expect(field, findsNothing);
    expect(find.text('from the keyboard'), findsOneWidget);
  });

  testWidgets('closes the sheet and shows the failure when sending fails',
      (tester) async {
    repo.onPush = (_, __) async => throw Exception('boom');
    await pumpFeed(tester);

    await send(tester, 'hello');

    expect(field, findsNothing);
    expect(
      find.text('Send failed: Something went wrong. Please try again.'),
      findsOneWidget,
    );
    expect(find.text('hello'), findsNothing);
    expect(find.text('body a'), findsOneWidget);
  });
}
