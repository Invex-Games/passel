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

/// The list before it has packets to show: loading, failed, and empty while
/// signed in or out.
void main() {
  late FakePacketRepository repo;
  late FakeAuthService auth;

  /// Pumps the list once auth has settled, so each test starts from a known
  /// auth state; the cold start before that has its own test file.
  Future<void> pumpList(WidgetTester tester, {bool signedIn = true}) async {
    auth = FakeAuthService(signedIn: signedIn);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => auth),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (_, ref, __) => ref.watch(authServiceProvider).hasValue
                ? const PacketList()
                : const SizedBox.shrink(),
          ),
        ),
      ),
    ));
  }

  final emptyHint =
      find.text('Nothing here yet.\nSend a link or a note from any device.');
  final signInPrompt = find.text('Sign in to see your links and notes');

  PacketsState page(List<String> ids) =>
      PacketsState(packets: ids.map(textPacket).toList());

  setUp(() {
    repo = FakePacketRepository();
  });

  testWidgets('shows a spinner until the first page arrives', (tester) async {
    final firstPage = Completer<PacketsState>();
    repo.onFetch = (_) => firstPage.future;
    await pumpList(tester);
    await tester.pump();

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.text('body a'), findsNothing);
    expect(emptyHint, findsNothing);

    firstPage.complete(page(['a']));
    await tester.pumpAndSettle();

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.text('body a'), findsOneWidget);
  });

  testWidgets('shows the failure with a Retry button that fetches again',
      (tester) async {
    var attempts = 0;
    repo.onFetch = (_) async {
      if (++attempts == 1) throw const SocketException('offline');
      return page(['a']);
    };
    await pumpList(tester);
    await tester.pumpAndSettle();

    expect(
      find.text('Could not reach Passel. Check your connection and try again.'),
      findsOneWidget,
    );
    expect(find.text('body a'), findsNothing);

    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();

    expect(repo.fetchCursors, [null, null]);
    expect(find.text('Retry'), findsNothing);
    expect(find.text('body a'), findsOneWidget);
  });

  testWidgets('signed in with nothing sent yet, invites a first send',
      (tester) async {
    repo.onFetch = (_) async => const PacketsState();
    await pumpList(tester);
    await tester.pumpAndSettle();

    expect(emptyHint, findsOneWidget);
    expect(signInPrompt, findsNothing);
  });

  testWidgets('signed out with nothing cached, asks to sign in, and signing '
      'in loads the feed', (tester) async {
    repo.onFetch = (_) async => page(['a']);
    await pumpList(tester, signedIn: false);
    await tester.pumpAndSettle();

    expect(find.byIcon(Icons.cloud_off), findsOneWidget);
    expect(signInPrompt, findsOneWidget);
    expect(emptyHint, findsNothing);
    expect(repo.fetchCursors, isEmpty);

    await tester.tap(find.text('Sign in'));
    await tester.pumpAndSettle();
    expect(find.text('Sign in to sync'), findsOneWidget);

    await tester.tap(find.text('Sign in with Google'));
    await tester.pumpAndSettle();

    expect(auth.signInCalls, 1);
    expect(find.text('Sign in to sync'), findsNothing);
    expect(repo.fetchCursors, [null]);
    expect(find.text('body a'), findsOneWidget);
    expect(signInPrompt, findsNothing);
  });

  testWidgets('signed out with an empty synced cache, shows the empty hint',
      (tester) async {
    repo.cache.state = PacketsState(lastSyncedAt: DateTime(2026, 10, 21));
    await pumpList(tester, signedIn: false);
    await tester.pumpAndSettle();

    expect(emptyHint, findsOneWidget);
    expect(signInPrompt, findsNothing);
    expect(repo.fetchCursors, isEmpty);
  });
}
