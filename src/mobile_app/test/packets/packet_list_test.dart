import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  Future<ProviderContainer> pumpList(WidgetTester tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: Scaffold(body: PacketList())),
    ));
    await tester.pumpAndSettle();
    return ProviderScope.containerOf(tester.element(find.byType(PacketList)));
  }

  setUp(() {
    repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(
            packets: [textPacket('a'), textPacket('b')],
          );
  });

  testWidgets('keeps the state of existing tiles when a packet arrives on top',
      (tester) async {
    final container = await pumpList(tester);
    final tileA = tester.state(find.byKey(const ValueKey('a')));
    final tileB = tester.state(find.byKey(const ValueKey('b')));

    await container
        .read(packetsProvider.notifier)
        .applyCreated(textPacket('new'));
    await tester.pumpAndSettle();

    expect(find.text('body new'), findsOneWidget);
    expect(tester.state(find.byKey(const ValueKey('a'))), same(tileA));
    expect(tester.state(find.byKey(const ValueKey('b'))), same(tileB));
  });

  testWidgets('keeps the state of the tiles below a deleted one',
      (tester) async {
    final container = await pumpList(tester);
    final tileB = tester.state(find.byKey(const ValueKey('b')));

    await container.read(packetsProvider.notifier).applyDeleted('a');
    await tester.pumpAndSettle();

    expect(find.text('body a'), findsNothing);
    expect(tester.state(find.byKey(const ValueKey('b'))), same(tileB));
  });
}
