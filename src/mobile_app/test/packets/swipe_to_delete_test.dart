import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/delete_packet.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  /// Pumps the list with the packets a, b and c, newest first.
  Future<ProviderContainer> pumpList(
    WidgetTester tester, {
    bool signedIn = true,
  }) async {
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
    return ProviderScope.containerOf(tester.element(find.byType(PacketList)));
  }

  /// The ids in the feed, newest first.
  List<String> listed(ProviderContainer container) => container
      .read(packetsProvider)
      .requireValue
      .packets
      .map((packet) => packet.id)
      .toList();

  /// Swipes the tile of [id] off the screen, as a finger would.
  Future<void> swipeAway(WidgetTester tester, String id) async {
    await tester.drag(find.text('body $id'), const Offset(-600, 0));
    await tester.pumpAndSettle();
  }

  /// Lets the undo window pass.
  Future<void> letUndoWindowPass(WidgetTester tester) async {
    await tester.pump(undoDeleteDuration + const Duration(seconds: 1));
    await tester.pumpAndSettle();
  }

  setUp(() {
    repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(
            packets: [textPacket('a'), textPacket('b'), textPacket('c')],
          );
  });

  testWidgets('swiping a tile away hides it and offers undo', (tester) async {
    final container = await pumpList(tester);

    await swipeAway(tester, 'b');

    expect(find.text('body b'), findsNothing);
    expect(listed(container), ['a', 'c']);
    expect(find.text('Note deleted'), findsOneWidget);
    expect(find.widgetWithText(SnackBarAction, 'Undo'), findsOneWidget);
    expect(repo.deletedIds, isEmpty);
  });

  testWidgets('undo puts the tile back between its neighbours, undeleted',
      (tester) async {
    final container = await pumpList(tester);
    await swipeAway(tester, 'b');

    await tester.tap(find.text('Undo'));
    await tester.pumpAndSettle();

    expect(find.text('body b'), findsOneWidget);
    expect(listed(container), ['a', 'b', 'c']);
    expect(find.byType(SnackBar), findsNothing);

    await letUndoWindowPass(tester);

    expect(repo.deletedIds, isEmpty);
    expect(find.text('body b'), findsOneWidget);
  });

  testWidgets('the deletion is sent once the undo window has passed',
      (tester) async {
    final container = await pumpList(tester);
    await swipeAway(tester, 'b');
    expect(repo.deletedIds, isEmpty);

    await letUndoWindowPass(tester);

    expect(repo.deletedIds, ['b']);
    expect(find.byType(SnackBar), findsNothing);
    expect(find.text('body b'), findsNothing);
    expect(listed(container), ['a', 'c']);
  });

  testWidgets('a failed deletion puts the swiped tile back', (tester) async {
    repo.onDelete = (_) async => throw const SocketException('offline');
    final container = await pumpList(tester);
    await swipeAway(tester, 'b');

    await letUndoWindowPass(tester);

    expect(repo.deletedIds, ['b']);
    expect(find.text('body b'), findsOneWidget);
    expect(listed(container), ['a', 'b', 'c']);
    expect(
      find.text('Couldn\'t delete the note: Could not reach Passel. Check '
          'your connection and try again.'),
      findsOneWidget,
    );
  });

  testWidgets('a tile only swipes toward the start', (tester) async {
    final container = await pumpList(tester);

    await tester.drag(find.text('body b'), const Offset(600, 0));
    await tester.pumpAndSettle();

    expect(find.text('body b'), findsOneWidget);
    expect(listed(container), ['a', 'b', 'c']);
    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('tiles cannot be swiped while signed out', (tester) async {
    repo.cache.state = PacketsState(packets: [textPacket('a'), textPacket('b')]);
    final container = await pumpList(tester, signedIn: false);
    expect(find.byType(Dismissible), findsNothing);

    await tester.drag(find.text('body b'), const Offset(-600, 0));
    await tester.pumpAndSettle();

    expect(find.text('body b'), findsOneWidget);
    expect(listed(container), ['a', 'b']);
    expect(repo.deletedIds, isEmpty);
  });
}
