import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';

import '../fakes.dart';

/// Loading older packets as the list is scrolled to its end.
void main() {
  late FakePacketRepository repo;

  /// The first page: ten packets, more than fit on the test screen.
  final firstPage = PacketsState(
    packets: [for (var i = 0; i < 10; i++) textPacket('p$i')],
    nextCursor: 'c1',
  );

  /// The second and last page.
  final secondPage = PacketsState(
    packets: [for (var i = 0; i < 5; i++) textPacket('q$i')],
  );

  Future<void> pumpList(WidgetTester tester, {bool signedIn = true}) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider
            .overrideWith(() => FakeAuthService(signedIn: signedIn)),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: Scaffold(body: PacketList())),
    ));
    await tester.pumpAndSettle();
  }

  final list = find.byType(ListView);
  final retryRow = find.text('Couldn\'t load older items. Tap to retry.');
  final endRow = find.text('You\'ve gone back to the beginning!');

  /// Scrolls down to the end of the list the way a finger would, stopping
  /// early if a sheet opens over the list.
  Future<void> scrollToEnd(WidgetTester tester) async {
    final position =
        tester.state<ScrollableState>(find.byType(Scrollable)).position;
    for (var i = 0; i < 30; i++) {
      if (position.extentAfter <= 0 ||
          find.byType(BottomSheet).evaluate().isNotEmpty) {
        return;
      }
      await tester.drag(list, const Offset(0, -400));
      await tester.pump();
    }
  }

  setUp(() {
    repo = FakePacketRepository()..onFetch = (_) async => firstPage;
  });

  testWidgets('reaching the end fetches the next page with the cursor',
      (tester) async {
    final nextPage = Completer<PacketsState>();
    repo.onFetch = (cursor) => cursor == null
        ? Future.value(firstPage)
        : nextPage.future;
    await pumpList(tester);
    expect(repo.fetchCursors, [null]);

    await scrollToEnd(tester);
    await tester.pump();

    expect(repo.fetchCursors, [null, 'c1']);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.text('body q0'), findsNothing);

    nextPage.complete(secondPage);
    await tester.pumpAndSettle();

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.text('body q0'), findsOneWidget);

    // Nothing older: the end row says so and no further page is fetched.
    await scrollToEnd(tester);
    await tester.pumpAndSettle();

    expect(endRow, findsOneWidget);
    expect(repo.fetchCursors, [null, 'c1']);
  });

  testWidgets('a failed page shows a retry row, and tapping it retries',
      (tester) async {
    var attempts = 0;
    repo.onFetch = (cursor) async {
      if (cursor == null) return firstPage;
      if (++attempts == 1) throw const SocketException('offline');
      return secondPage;
    };
    await pumpList(tester);

    await scrollToEnd(tester);
    await tester.pumpAndSettle();

    expect(retryRow, findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(repo.fetchCursors, [null, 'c1']);

    // Scrolling again does not hammer the server; the row waits for a tap.
    await tester.drag(list, const Offset(0, -200));
    await tester.pumpAndSettle();
    expect(repo.fetchCursors, [null, 'c1']);

    await tester.tap(retryRow);
    await tester.pumpAndSettle();

    expect(repo.fetchCursors, [null, 'c1', 'c1']);
    expect(retryRow, findsNothing);
    expect(find.text('body q0'), findsOneWidget);
  });

  testWidgets('signed out, the end row offers sign-in and nothing is fetched',
      (tester) async {
    repo.cache.state = firstPage;
    await pumpList(tester, signedIn: false);
    expect(find.text('body p0'), findsOneWidget);

    // The first scroll to the end prompts once...
    await scrollToEnd(tester);
    await tester.pumpAndSettle();
    expect(find.text('Sign in to sync'), findsOneWidget);

    await tester.tap(find.text('Maybe later'));
    await tester.pumpAndSettle();
    expect(find.text('Sign in to sync'), findsNothing);

    // ...and after that the end row offers it instead.
    await scrollToEnd(tester);
    await tester.pumpAndSettle();
    expect(find.text('Sign in to load older items'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);

    await tester.tap(find.text('Sign in'));
    await tester.pumpAndSettle();
    expect(find.text('Sign in to sync'), findsOneWidget);

    expect(repo.fetchCursors, isEmpty);
  });

  testWidgets('the end row says so when there is nothing older',
      (tester) async {
    repo.onFetch = (_) async => firstPage.copyWith(nextCursor: () => null);
    await pumpList(tester);

    await scrollToEnd(tester);
    await tester.pumpAndSettle();

    expect(endRow, findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(repo.fetchCursors, [null]);
  });
}
