import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_feed_page.dart';

import '../fakes.dart';

void main() {
  /// Pumps the feed with [count] packets. The list is reversed, newest at
  /// the bottom, and the default count is enough to scroll.
  Future<void> pumpFeed(WidgetTester tester, {int count = 40}) async {
    final repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(
            packets: [for (var i = 0; i < count; i++) textPacket('p$i')],
          );
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

  /// The current height of the collapsing app bar.
  double appBarHeight(WidgetTester tester) =>
      tester.getSize(find.byType(AnimatedContainer).first).height;

  final list = find.byType(ListView);

  testWidgets('the newest packet is at the top', (tester) async {
    await pumpFeed(tester, count: 3);

    // The repository lists newest first, so the first packet is the newest.
    final top = tester.getTopLeft(find.text('body p0')).dy;
    final below = tester.getTopLeft(find.text('body p1')).dy;
    expect(top, lessThan(below));
  });

  testWidgets(
      'scrolling down through older packets hides the app bar, and scrolling '
      'back up shows it again', (tester) async {
    await pumpFeed(tester);
    expect(appBarHeight(tester), kToolbarHeight);

    // Finger up: the list scrolls down, toward older packets.
    await tester.drag(list, const Offset(0, -300));
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), 0);

    // Finger down: back toward the newest, without reaching the top.
    await tester.drag(list, const Offset(0, 100));
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), kToolbarHeight);
  });

  testWidgets('a pull to refresh at the top keeps the app bar',
      (tester) async {
    await pumpFeed(tester);

    // At the top the pull only overscrolls. A finger wobbles, so the drag
    // direction flips for a moment on the way.
    final gesture = await tester.startGesture(tester.getCenter(list));
    await gesture.moveBy(const Offset(0, 150));
    await tester.pump();
    await gesture.moveBy(const Offset(0, -4));
    await tester.pump();
    await gesture.moveBy(const Offset(0, 40));
    await tester.pump();
    expect(appBarHeight(tester), kToolbarHeight);

    await gesture.up();
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), kToolbarHeight);
  });

  testWidgets('the empty feed invites a first send', (tester) async {
    await pumpFeed(tester, count: 0);

    expect(
      find.text('Nothing here yet.\nSend a link or a note from any device.'),
      findsOneWidget,
    );
  });
}
