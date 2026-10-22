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

  testWidgets(
      'scrolling back through older packets hides the app bar, and scrolling '
      'toward the newest shows it again', (tester) async {
    await pumpFeed(tester);
    expect(appBarHeight(tester), kToolbarHeight);

    // Finger down: toward older packets in the reversed list.
    await tester.drag(list, const Offset(0, 300));
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), 0);

    // Finger up: back toward the newest, without reaching it.
    await tester.drag(list, const Offset(0, -100));
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), kToolbarHeight);
  });

  testWidgets('a pull to refresh at the newest end keeps the app bar',
      (tester) async {
    await pumpFeed(tester);

    // At the bottom edge the pull only overscrolls. A finger wobbles, so the
    // drag direction flips for a moment on the way.
    final gesture = await tester.startGesture(tester.getCenter(list));
    await gesture.moveBy(const Offset(0, -150));
    await tester.pump();
    await gesture.moveBy(const Offset(0, 4));
    await tester.pump();
    await gesture.moveBy(const Offset(0, -40));
    await tester.pump();
    expect(appBarHeight(tester), kToolbarHeight);

    await gesture.up();
    await tester.pumpAndSettle();
    expect(appBarHeight(tester), kToolbarHeight);
  });

  testWidgets('the empty feed says which way to pull', (tester) async {
    await pumpFeed(tester, count: 0);

    expect(find.text('No packets yet.\nPull up to refresh.'), findsOneWidget);
  });
}
