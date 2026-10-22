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

/// A cold start without a cache: the list is built while Google Sign-In is
/// still initialising, so the feed first loads signed out as a blank state,
/// and the signed-in fetch that follows must show as loading, or as failed,
/// rather than as an empty feed.
void main() {
  late FakePacketRepository repo;
  late PendingAuthService auth;
  late Completer<PacketsState> firstPage;

  final emptyHint =
      find.text('Nothing here yet.\nSend a link or a note from any device.');
  final signInPrompt = find.text('Sign in to see your links and notes');

  /// Pumps the list before auth has settled, then settles it signed in with
  /// the first fetch still pending.
  Future<void> pumpColdStart(WidgetTester tester) async {
    firstPage = Completer<PacketsState>();
    repo = FakePacketRepository();
    repo.onFetch = (_) => firstPage.future;
    auth = PendingAuthService();
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => auth),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: Scaffold(body: PacketList())),
    ));
    // The empty cache is read and the signed-out state lands.
    await tester.pump();
    await tester.pump();
    expect(signInPrompt, findsOneWidget);

    // Auth settles signed in: the feed rebuilds and fetches.
    auth.complete(true);
    await tester.pump();
    await tester.pump();
  }

  testWidgets('shows the spinner, not the empty hint, while the first fetch runs',
      (tester) async {
    await pumpColdStart(tester);

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(emptyHint, findsNothing);
    expect(signInPrompt, findsNothing);

    firstPage.complete(PacketsState(
      packets: [textPacket('a')],
      lastSyncedAt: DateTime.now(),
    ));
    await tester.pump();
    await tester.pumpAndSettle();

    expect(find.text('body a'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('shows the failure with Retry when the first fetch fails',
      (tester) async {
    await pumpColdStart(tester);

    firstPage.completeError(const SocketException('offline'));
    await tester.pump();
    await tester.pump();

    expect(find.text('Retry'), findsOneWidget);
    expect(emptyHint, findsNothing);
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });
}
