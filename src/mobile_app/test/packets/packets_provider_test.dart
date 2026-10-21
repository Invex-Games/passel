import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_helpers.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  /// Creates a container with a fake repository and auth, and waits until
  /// the first [packetsProvider] build has completed.
  Future<ProviderContainer> start({
    bool signedIn = true,
    FakeAuthService? auth,
  }) async {
    final container = ProviderContainer(overrides: [
      authServiceProvider
          .overrideWith(() => auth ?? FakeAuthService(signedIn: signedIn)),
      packetRepositoryProvider.overrideWithValue(repo),
    ]);
    addTearDown(container.dispose);

    await container.read(authServiceProvider.future);
    // Keep the provider alive / listened like the UI does.
    container.listen(packetsProvider, (_, __) {});
    await container.read(packetsProvider.future);
    return container;
  }

  PacketsState stateOf(ProviderContainer c) =>
      c.read(packetsProvider).requireValue;

  List<String> idsOf(ProviderContainer c) =>
      stateOf(c).packets.map((p) => p.id).toList();

  PacketsState page(List<String> ids, {String? next}) => PacketsState(
        packets: ids.map(textPacket).toList(),
        nextCursor: next,
        lastSyncedAt: DateTime(2026, 10, 21),
      );

  setUp(() {
    repo = FakePacketRepository();
  });

  group('PacketsNotifier.build', () {
    test('fetches the first page when there is no cache and saves it',
        () async {
      repo.onFetch = (_) async => page(['a', 'b'], next: 'c1');

      final c = await start();

      expect(idsOf(c), ['a', 'b']);
      expect(stateOf(c).nextCursor, 'c1');
      expect(repo.cache.state?.packets.map((p) => p.id), ['a', 'b']);
    });

    test('serves the cache when signed out without hitting the network',
        () async {
      repo.cache.state = page(['cached']);

      final c = await start(signedIn: false);

      expect(idsOf(c), ['cached']);
      expect(repo.fetchCursors, isEmpty);
    });

    test('rebuilds when the auth state changes', () async {
      repo.cache.state = page(['cached']);
      final auth = FakeAuthService(signedIn: false);
      final c = await start(auth: auth);
      expect(repo.fetchCursors, isEmpty);

      repo.onFetch = (_) async => page(['fresh']);
      await auth.signIn();
      await c.read(packetsProvider.future);
      await pumpEventQueue();

      expect(repo.fetchCursors, [null]);
      expect(idsOf(c), ['fresh']);
    });
  });

  group('PacketsNotifier.loadMore', () {
    test('appends the next page and de-duplicates by id', () async {
      repo.onFetch = (cursor) async => cursor == null
          ? page(['a', 'b'], next: 'c1')
          : page(['b', 'c'], next: 'c2');
      final c = await start();

      final authed = await c.read(packetsProvider.notifier).loadMore();

      expect(authed, isTrue);
      expect(repo.fetchCursors, [null, 'c1']);
      expect(idsOf(c), ['a', 'b', 'c']);
      expect(stateOf(c).nextCursor, 'c2');
      expect(stateOf(c).isLoadingMore, isFalse);
      expect(repo.cache.state?.packets.map((p) => p.id), ['a', 'b', 'c']);
    });

    test('ignores calls while a load is already in flight', () async {
      final pending = Completer<PacketsState>();
      repo.onFetch = (cursor) =>
          cursor == null ? Future.value(page(['a'], next: 'c1')) : pending.future;
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final first = notifier.loadMore();
      await notifier.loadMore();
      pending.complete(page(['b']));
      await first;

      expect(repo.fetchCursors, [null, 'c1']);
      expect(idsOf(c), ['a', 'b']);
    });

    test('discards a stale page when a refresh happened meanwhile', () async {
      final pendingPage2 = Completer<PacketsState>();
      var firstPageCalls = 0;
      repo.onFetch = (cursor) {
        if (cursor == null) {
          firstPageCalls++;
          return Future.value(firstPageCalls == 1
              ? page(['a', 'b'], next: 'c1')
              : page(['x', 'y'], next: 'c9'));
        }
        return pendingPage2.future;
      };
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final loadMore = notifier.loadMore();
      expect(stateOf(c).isLoadingMore, isTrue);

      await notifier.refresh();
      expect(idsOf(c), ['x', 'y']);

      // The old page arrives after the refresh — it must not be merged.
      pendingPage2.complete(page(['old'], next: 'c2'));
      await loadMore;

      expect(idsOf(c), ['x', 'y']);
      expect(stateOf(c).nextCursor, 'c9');
      expect(stateOf(c).isLoadingMore, isFalse);
      expect(repo.cache.state?.packets.map((p) => p.id), ['x', 'y']);
    });

    test('merges into the latest list so a concurrent push is kept',
        () async {
      final pendingPage2 = Completer<PacketsState>();
      repo.onFetch = (cursor) => cursor == null
          ? Future.value(page(['a'], next: 'c1'))
          : pendingPage2.future;
      repo.onPush = (_, __) async => textPacket('pushed');
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final loadMore = notifier.loadMore();
      await notifier.push(type: PacketType.text, payload: 'hi');
      pendingPage2.complete(page(['b']));
      await loadMore;

      expect(idsOf(c), ['pushed', 'a', 'b']);
    });

    test('stores the error, stops auto-retrying, and retries on demand',
        () async {
      var failPage2 = true;
      repo.onFetch = (cursor) async {
        if (cursor == null) return page(['a'], next: 'c1');
        if (failPage2) throw Exception('offline');
        return page(['b']);
      };
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      await notifier.loadMore();

      expect(stateOf(c).loadMoreError, isA<Exception>());
      expect(stateOf(c).isLoadingMore, isFalse);
      expect(idsOf(c), ['a']); // existing data stays visible
      expect(stateOf(c).hasMore, isTrue);

      // Scroll-triggered calls don't retry while the error is pending.
      await notifier.loadMore();
      expect(repo.fetchCursors, [null, 'c1']);

      // An explicit retry does, and clears the error on success.
      failPage2 = false;
      await notifier.loadMore(retry: true);
      expect(repo.fetchCursors, [null, 'c1', 'c1']);
      expect(stateOf(c).loadMoreError, isNull);
      expect(idsOf(c), ['a', 'b']);
      expect(stateOf(c).hasMore, isFalse);
    });

    test('returns false when the session expired', () async {
      repo.onFetch = (cursor) async {
        if (cursor == null) return page(['a'], next: 'c1');
        throw const SessionExpiredException();
      };
      final c = await start();

      final authed = await c.read(packetsProvider.notifier).loadMore();

      expect(authed, isFalse);
      expect(stateOf(c).loadMoreError, isA<SessionExpiredException>());
    });

    test('returns false without fetching when signed out', () async {
      repo.cache.state = page(['a'], next: 'c1');
      final c = await start(signedIn: false);

      final authed = await c.read(packetsProvider.notifier).loadMore();

      expect(authed, isFalse);
      expect(repo.fetchCursors, isEmpty);
    });

    test('stops paging if the server repeats the same cursor', () async {
      repo.onFetch = (cursor) async => cursor == null
          ? page(['a'], next: 'same')
          : page(['b'], next: 'same');
      final c = await start();

      await c.read(packetsProvider.notifier).loadMore();

      expect(stateOf(c).hasMore, isFalse);
    });
  });

  group('PacketsNotifier.push', () {
    test('prepends the created packet without re-fetching', () async {
      repo.onFetch = (cursor) async => cursor == null
          ? page(['a', 'b'], next: 'c1')
          : page(['c']);
      repo.onPush = (type, payload) async => Packet(
            id: 'new',
            type: type,
            payload: payload,
          );
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.loadMore();

      final authed = await notifier.push(
        type: PacketType.link,
        payload: 'https://example.com',
      );

      expect(authed, isTrue);
      expect(idsOf(c), ['new', 'a', 'b', 'c']); // loaded pages are kept
      expect(stateOf(c).packets.first.type, PacketType.link);
      expect(repo.fetchCursors, [null, 'c1']);
      expect(repo.cache.state?.packets.first.id, 'new');
    });

    test('does not duplicate a packet that is already listed', () async {
      repo.onFetch = (_) async => page(['a', 'b']);
      repo.onPush = (_, __) async => textPacket('b');
      final c = await start();

      await c
          .read(packetsProvider.notifier)
          .push(type: PacketType.text, payload: 'x');

      expect(idsOf(c), ['b', 'a']);
    });

    test('falls back to a refresh when the response has no packet', () async {
      var firstPageCalls = 0;
      repo.onFetch = (_) async =>
          ++firstPageCalls == 1 ? page(['a']) : page(['new', 'a']);
      repo.onPush = (_, __) async => null;
      final c = await start();

      await c
          .read(packetsProvider.notifier)
          .push(type: PacketType.text, payload: 'x');

      expect(idsOf(c), ['new', 'a']);
    });

    test('returns false when signed out', () async {
      final c = await start(signedIn: false);

      final authed = await c
          .read(packetsProvider.notifier)
          .push(type: PacketType.text, payload: 'x');

      expect(authed, isFalse);
    });
  });

  group('PacketsNotifier.refresh', () {
    test('a slower, older refresh does not overwrite a newer one', () async {
      final slow = Completer<PacketsState>();
      var calls = 0;
      repo.onFetch = (_) {
        calls++;
        if (calls == 1) return Future.value(page(['initial']));
        if (calls == 2) return slow.future;
        return Future.value(page(['newest']));
      };
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final older = notifier.refresh();
      await notifier.refresh();
      slow.complete(page(['stale']));
      await older;

      expect(idsOf(c), ['newest']);
    });

    test('propagates failures to the caller', () async {
      var calls = 0;
      repo.onFetch = (_) async {
        if (++calls == 1) return page(['a']);
        throw Exception('offline');
      };
      final c = await start();

      await expectLater(
        c.read(packetsProvider.notifier).refresh(),
        throwsA(isA<Exception>()),
      );
      expect(idsOf(c), ['a']); // existing data stays
    });
  });

  group('hasCachedPacketsProvider', () {
    test('is re-evaluated after packets are saved', () async {
      repo.onFetch = (_) async => page(['a']);
      final container = ProviderContainer(overrides: [
        authServiceProvider
            .overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
      ]);
      addTearDown(container.dispose);
      container.listen(hasCachedPacketsProvider, (_, __) {});

      await container.read(authServiceProvider.future);
      expect(await container.read(hasCachedPacketsProvider.future), isFalse);

      container.listen(packetsProvider, (_, __) {});
      await container.read(packetsProvider.future);
      await pumpEventQueue();

      expect(await container.read(hasCachedPacketsProvider.future), isTrue);
    });
  });
}
