import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/delete_packet.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';
import 'package:passel_mobileapp/packets/widgets/packet_tile.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  Future<void> pumpFeed(WidgetTester tester, {bool signedIn = true}) async {
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

  Finder deleteButtonOf(String id) => find.descendant(
        of: find.ancestor(
          of: find.text('body $id'),
          matching: find.byType(PacketTile),
        ),
        matching: find.byTooltip('Delete'),
      );

  setUp(() {
    repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(
            packets: [textPacket('a'), textPacket('b')],
          );
  });

  testWidgets('undo puts the packet back without deleting it',
      (tester) async {
    await pumpFeed(tester);

    await tester.tap(deleteButtonOf('a'));
    await tester.pumpAndSettle();

    expect(find.text('body a'), findsNothing);
    expect(find.text('Packet deleted'), findsOneWidget);

    await tester.tap(find.text('Undo'));
    await tester.pumpAndSettle();

    expect(find.text('body a'), findsOneWidget);
    expect(repo.deletedIds, isEmpty);
  });

  testWidgets('deletes on the server once the snackbar times out',
      (tester) async {
    await pumpFeed(tester);

    await tester.tap(deleteButtonOf('a'));
    await tester.pumpAndSettle();
    expect(repo.deletedIds, isEmpty);

    await tester.pump(undoDeleteDuration + const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(find.text('Packet deleted'), findsNothing);
    expect(repo.deletedIds, ['a']);
    expect(find.text('body a'), findsNothing);
  });

  testWidgets('a second deletion commits the first straight away',
      (tester) async {
    await pumpFeed(tester);

    await tester.tap(deleteButtonOf('a'));
    await tester.pumpAndSettle();
    await tester.tap(deleteButtonOf('b'));
    await tester.pumpAndSettle();

    expect(repo.deletedIds, ['a']);
  });

  testWidgets('swiping a tile away deletes it with undo', (tester) async {
    await pumpFeed(tester);

    await tester.drag(find.text('body b'), const Offset(-600, 0));
    await tester.pumpAndSettle();

    expect(find.text('body b'), findsNothing);
    expect(find.text('Packet deleted'), findsOneWidget);
  });

  testWidgets('shows the failure in plain words and restores the packet',
      (tester) async {
    repo.onDelete = (_) async => throw const SocketException('offline');
    await pumpFeed(tester);

    await tester.tap(deleteButtonOf('a'));
    await tester.pumpAndSettle();
    await tester.pump(undoDeleteDuration + const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(
      find.text('Couldn\'t delete packet: Could not reach Passel. Check your '
          'connection and try again.'),
      findsOneWidget,
    );
    expect(find.text('body a'), findsOneWidget);
  });

  testWidgets('offers no deletion while signed out', (tester) async {
    repo.cache.state = PacketsState(packets: [textPacket('a')]);
    await pumpFeed(tester, signedIn: false);

    expect(find.text('body a'), findsOneWidget);
    expect(find.byTooltip('Delete'), findsNothing);
  });
}
