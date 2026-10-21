import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_helpers.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;
  late FakeAppForeground foreground;

  /// Creates a container whose [packetLiveUpdatesProvider] is watched like
  /// the feed page does.  Call [pumpEventQueue] (or flush microtasks) to let
  /// auth and the first build settle.
  ProviderContainer start({bool signedIn = true, bool inForeground = true}) {
    foreground = FakeAppForeground(initial: inForeground);
    final container = ProviderContainer(overrides: [
      authServiceProvider
          .overrideWith(() => FakeAuthService(signedIn: signedIn)),
      packetRepositoryProvider.overrideWithValue(repo),
      appForegroundProvider.overrideWith(() => foreground),
    ]);
    container.listen(packetsProvider, (_, __) {});
    container.listen(packetLiveUpdatesProvider, (_, __) {});
    return container;
  }

  bool isLive(ProviderContainer c) => c.read(packetLiveUpdatesProvider);

  List<String> idsOf(ProviderContainer c) =>
      c.read(packetsProvider).requireValue.packets.map((p) => p.id).toList();

  PacketsState page(List<String> ids) =>
      PacketsState(packets: ids.map(textPacket).toList());

  setUp(() {
    repo = FakePacketRepository();
  });

  test('connects, goes live on resync and catches up', () async {
    var calls = 0;
    repo.onFetch = (_) async => ++calls == 1 ? page(['a']) : page(['new', 'a']);
    final c = start();
    addTearDown(c.dispose);
    await pumpEventQueue();

    expect(repo.connections, hasLength(1));
    expect(isLive(c), isFalse);

    repo.connections.single.add(const PacketsResyncEvent());
    await pumpEventQueue();

    expect(isLive(c), isTrue);
    expect(repo.fetchCursors, [null, null]);
    expect(idsOf(c), ['new', 'a']);
  });

  test('applies packets created elsewhere without fetching', () async {
    repo.onFetch = (_) async => page(['a']);
    final c = start();
    addTearDown(c.dispose);
    await pumpEventQueue();
    repo.connections.single.add(const PacketsResyncEvent());
    await pumpEventQueue();

    repo.connections.single.add(PacketCreatedEvent(textPacket('live')));
    await pumpEventQueue();

    expect(idsOf(c), ['live', 'a']);
    expect(repo.fetchCursors, [null, null]);
  });

  test('does not connect when signed out', () async {
    repo.cache.state = page(['a']);
    final c = start(signedIn: false);
    addTearDown(c.dispose);
    await pumpEventQueue();

    expect(repo.connections, isEmpty);
  });

  test('disconnects in the background and catches up on return', () async {
    repo.onFetch = (_) async => page(['a']);
    final c = start();
    addTearDown(c.dispose);
    await pumpEventQueue();
    repo.connections.single.add(const PacketsResyncEvent());
    await pumpEventQueue();

    foreground.setForeground(false);
    await pumpEventQueue();

    expect(repo.connections.single.hasListener, isFalse);
    expect(isLive(c), isFalse);

    repo.onFetch = (_) async => page(['while-away', 'a']);
    foreground.setForeground(true);
    await pumpEventQueue();
    expect(repo.connections, hasLength(2));
    repo.connections.last.add(const PacketsResyncEvent());
    await pumpEventQueue();

    expect(isLive(c), isTrue);
    expect(idsOf(c), ['while-away', 'a']);
  });

  test('syncs directly when the stream cannot connect', () async {
    var calls = 0;
    repo.onFetch = (_) async => ++calls == 1 ? page(['a']) : page(['new', 'a']);
    final c = start();
    addTearDown(c.dispose);
    await pumpEventQueue();

    repo.connections.single.addError(Exception('offline'));
    await pumpEventQueue();

    expect(isLive(c), isFalse);
    expect(idsOf(c), ['new', 'a']);
  });

  test('reconnects with growing delays', () {
    fakeAsync((async) {
      repo = FakePacketRepository();
      final c = start();
      async.elapse(Duration.zero);
      repo.connections.single.add(const PacketsResyncEvent());
      async.elapse(Duration.zero);

      // The server closed the connection: retry within a second.
      repo.connections.single.close();
      async.elapse(Duration.zero);
      expect(isLive(c), isFalse);
      async.elapse(const Duration(seconds: 1));
      expect(repo.connections, hasLength(2));

      // A second failure without a successful resync backs off further.
      repo.connections.last.addError(Exception('offline'));
      async.elapse(const Duration(seconds: 1));
      expect(repo.connections, hasLength(2));
      async.elapse(const Duration(seconds: 1));
      expect(repo.connections, hasLength(3));

      // Backoff is capped.
      for (var i = 0; i < 10; i++) {
        repo.connections.last.addError(Exception('offline'));
        async.elapse(PacketLiveUpdates.maxRetryDelay);
      }
      expect(repo.connections, hasLength(13));

      c.dispose();
    });
  });

  test('stops retrying when the session expired', () {
    fakeAsync((async) {
      repo = FakePacketRepository();
      final c = start();
      async.elapse(Duration.zero);

      repo.connections.single.addError(const SessionExpiredException());
      async.elapse(const Duration(minutes: 5));

      expect(repo.connections, hasLength(1));
      c.dispose();
    });
  });

  test('reconnects when catching up after a resync fails', () {
    fakeAsync((async) {
      repo = FakePacketRepository();
      var calls = 0;
      repo.onFetch = (_) async {
        if (++calls == 2) throw Exception('offline');
        return page(['a']);
      };
      final c = start();
      async.elapse(Duration.zero);

      repo.connections.single.add(const PacketsResyncEvent());
      async.elapse(Duration.zero);

      expect(repo.connections.single.hasListener, isFalse);
      expect(isLive(c), isFalse);
      async.elapse(const Duration(seconds: 1));
      expect(repo.connections, hasLength(2));

      c.dispose();
    });
  });
}
