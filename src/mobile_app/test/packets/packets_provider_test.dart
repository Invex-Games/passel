import 'dart:async';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_helpers.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_cache.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  /// Creates a container with a fake repository and auth, and waits until
  /// the first [packetsProvider] build has completed.  Cache writes happen
  /// on the next event-loop turn; `await pumpEventQueue()` before checking
  /// the cache.
  Future<ProviderContainer> start({
    bool signedIn = true,
    FakeAuthService? auth,
  }) async {
    final container = ProviderContainer(overrides: [
      authServiceProvider
          .overrideWith(() => auth ?? FakeAuthService(signedIn: signedIn)),
      packetRepositoryProvider.overrideWithValue(repo),
      immediateCacheWrites(),
    ]);
    addTearDown(container.dispose);

    await container.read(authServiceProvider.future);
    // Keep the provider alive / listened like the UI does.
    container.listen(packetsProvider, (_, __) {});
    await container.read(packetsProvider.future);
    return container;
  }

  /// Creates a container inside [fakeAsync], with the real cache write
  /// delay; `async.elapse` drives it.
  ProviderContainer startInFakeAsync(FakeAsync async, {FakeAuthService? auth}) {
    final container = ProviderContainer(overrides: [
      authServiceProvider
          .overrideWith(() => auth ?? FakeAuthService(signedIn: true)),
      packetRepositoryProvider.overrideWithValue(repo),
    ]);
    container.listen(packetsProvider, (_, __) {});
    async.elapse(Duration.zero);
    return container;
  }

  PacketsState stateOf(ProviderContainer c) =>
      c.read(packetsProvider).requireValue;

  List<String> idsOf(ProviderContainer c) =>
      stateOf(c).packets.map((p) => p.id).toList();

  List<String>? cachedIds() =>
      repo.cache.state?.packets.map((p) => p.id).toList();

  PacketsState page(List<String> ids, {String? next}) => PacketsState(
        packets: ids.map(textPacket).toList(),
        nextCursor: next,
        lastSyncedAt: DateTime(2026, 10, 21),
      );

  /// `prefix1` … `prefixN`.
  List<String> ids(String prefix, int n) =>
      List.generate(n, (i) => '$prefix${i + 1}');

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
      await pumpEventQueue();
      expect(cachedIds(), ['a', 'b']);
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
      await pumpEventQueue();
      expect(cachedIds(), ['a', 'b', 'c']);
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
      await pumpEventQueue();
      expect(cachedIds(), ['x', 'y']);
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
      await pumpEventQueue();
      expect(cachedIds()?.first, 'new');
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

  group('PacketsNotifier.applyCreated', () {
    test('prepends the packet, keeps loaded pages, and saves', () async {
      repo.onFetch = (cursor) async =>
          cursor == null ? page(['a'], next: 'c1') : page(['b']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.loadMore();

      await notifier.applyCreated(textPacket('live'));

      expect(idsOf(c), ['live', 'a', 'b']);
      await pumpEventQueue();
      expect(cachedIds()?.first, 'live');
      expect(repo.fetchCursors, [null, 'c1']); // no extra fetch
    });

    test('leaves an already-listed packet in place', () async {
      repo.onFetch = (_) async => page(['a', 'b']);
      final c = await start();

      await c.read(packetsProvider.notifier).applyCreated(textPacket('b'));

      expect(idsOf(c), ['a', 'b']);
    });

    test('is ignored when signed out', () async {
      repo.cache.state = page(['a']);
      final c = await start(signedIn: false);

      await c.read(packetsProvider.notifier).applyCreated(textPacket('live'));

      expect(idsOf(c), ['a']);
    });

    test('survives a refresh whose page predates it', () async {
      final pending = Completer<PacketsState>();
      var calls = 0;
      repo.onFetch = (_) =>
          ++calls == 1 ? Future.value(page(['a'])) : pending.future;
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final refresh = notifier.refresh();
      await notifier.applyCreated(textPacket('live'));
      pending.complete(page(['new', 'a']));
      await refresh;

      expect(idsOf(c), ['live', 'new', 'a']);
      await pumpEventQueue();
      expect(cachedIds(), ['live', 'new', 'a']);
    });

    test('is included when it arrives while the first fetch is in flight',
        () async {
      final pending = Completer<PacketsState>();
      repo.onFetch = (_) => pending.future;
      final c = ProviderContainer(overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
      ]);
      addTearDown(c.dispose);
      await c.read(authServiceProvider.future);
      c.listen(packetsProvider, (_, __) {});
      await pumpEventQueue();

      await c.read(packetsProvider.notifier).applyCreated(textPacket('live'));
      pending.complete(page(['a']));
      await c.read(packetsProvider.future);

      expect(idsOf(c), ['live', 'a']);
    });
  });

  group('PacketsNotifier.syncLatest', () {
    test('merges the first page on top, keeping loaded pages and cursor',
        () async {
      var firstPageCalls = 0;
      repo.onFetch = (cursor) async {
        if (cursor == 'c1') return page(['c', 'd'], next: 'c2');
        return ++firstPageCalls == 1
            ? page(['a', 'b'], next: 'c1')
            : page(['new', 'a'], next: 'x');
      };
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.loadMore();

      await notifier.syncLatest();

      expect(idsOf(c), ['new', 'a', 'b', 'c', 'd']);
      expect(stateOf(c).nextCursor, 'c2');
      await pumpEventQueue();
      expect(cachedIds()?.first, 'new');
    });

    test('replaces the list when the page does not overlap it', () async {
      var calls = 0;
      repo.onFetch = (_) async =>
          ++calls == 1 ? page(['a'], next: 'c1') : page(['x', 'y'], next: 'c9');
      final c = await start();

      await c.read(packetsProvider.notifier).syncLatest();

      expect(idsOf(c), ['x', 'y']);
      expect(stateOf(c).nextCursor, 'c9');
    });

    test('fetches again after an in-flight build completes', () async {
      final pending = Completer<PacketsState>();
      var calls = 0;
      repo.onFetch = (_) =>
          ++calls == 1 ? pending.future : Future.value(page(['new', 'a']));
      final c = ProviderContainer(overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
      ]);
      addTearDown(c.dispose);
      await c.read(authServiceProvider.future);
      c.listen(packetsProvider, (_, __) {});
      await pumpEventQueue();

      final sync = c.read(packetsProvider.notifier).syncLatest();
      pending.complete(page(['a']));
      await sync;

      expect(repo.fetchCursors, [null, null]);
      expect(idsOf(c), ['new', 'a']);
    });

    test('keeps a packet that arrived while it was fetching', () async {
      final pending = Completer<PacketsState>();
      var calls = 0;
      repo.onFetch = (_) =>
          ++calls == 1 ? Future.value(page(['a'])) : pending.future;
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      final sync = notifier.syncLatest();
      await notifier.applyCreated(textPacket('live'));
      pending.complete(page(['new', 'a']));
      await sync;

      expect(idsOf(c), ['live', 'new', 'a']);
    });

    test('does nothing when signed out', () async {
      repo.cache.state = page(['a']);
      final c = await start(signedIn: false);

      await c.read(packetsProvider.notifier).syncLatest();

      expect(repo.fetchCursors, isEmpty);
    });
  });

  group('PacketsNotifier deletion', () {
    test('hide removes the packet until unhide puts it back in place',
        () async {
      repo.onFetch = (_) async => page(['a', 'b', 'c']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);

      expect(notifier.hide('b'), isTrue);
      expect(idsOf(c), ['a', 'c']);
      await pumpEventQueue();
      expect(cachedIds(), ['a', 'c']);

      notifier.unhide('b');

      expect(idsOf(c), ['a', 'b', 'c']);
      expect(repo.deletedIds, isEmpty);
    });

    test('hide returns false for a packet that is not listed', () async {
      repo.onFetch = (_) async => page(['a']);
      final c = await start();

      expect(c.read(packetsProvider.notifier).hide('x'), isFalse);
    });

    test('delete removes the packet on the server', () async {
      repo.onFetch = (_) async => page(['a', 'b']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      notifier.hide('a');

      final authed = await notifier.delete('a');

      expect(authed, isTrue);
      expect(repo.deletedIds, ['a']);
      expect(idsOf(c), ['b']);

      // Committed: undo no longer applies.
      notifier.unhide('a');
      expect(idsOf(c), ['b']);
    });

    test('delete puts the packet back and rethrows when it fails', () async {
      repo.onFetch = (_) async => page(['a', 'b']);
      repo.onDelete = (_) async => throw Exception('offline');
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      notifier.hide('a');

      await expectLater(notifier.delete('a'), throwsA(isA<Exception>()));

      expect(idsOf(c), ['a', 'b']);
    });

    test('delete returns false and keeps the packet when signed out',
        () async {
      repo.cache.state = page(['a']);
      final c = await start(signedIn: false);

      final authed = await c.read(packetsProvider.notifier).delete('a');

      expect(authed, isFalse);
      expect(idsOf(c), ['a']);
      expect(repo.deletedIds, isEmpty);
    });

    test('a hidden packet stays hidden when a refresh brings it back',
        () async {
      var calls = 0;
      repo.onFetch = (_) async =>
          ++calls == 1 ? page(['a', 'b']) : page(['new', 'a', 'b']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      notifier.hide('a');

      await notifier.refresh();

      expect(idsOf(c), ['new', 'b']);

      notifier.unhide('a');
      expect(idsOf(c), ['new', 'a', 'b']);
    });

    test('a page loaded after a deletion does not bring it back', () async {
      repo.onFetch = (cursor) async =>
          cursor == null ? page(['a'], next: 'c1') : page(['b', 'c']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.applyDeleted('b');

      await notifier.loadMore();

      expect(idsOf(c), ['a', 'c']);
    });

    test('applyDeleted removes a packet deleted elsewhere for good',
        () async {
      repo.onFetch = (_) async => page(['a', 'b']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      notifier.hide('a');

      await notifier.applyDeleted('a');
      await notifier.applyDeleted('b');
      notifier.unhide('a');

      expect(idsOf(c), isEmpty);
      await pumpEventQueue();
      expect(cachedIds(), isEmpty);
    });

    test('applyCreated ignores a packet that was already deleted', () async {
      repo.onFetch = (_) async => page(['a']);
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.applyDeleted('late');

      await notifier.applyCreated(textPacket('late'));

      expect(idsOf(c), ['a']);
    });
  });

  group('cachedPacketsProvider', () {
    test('is parsed once and shared with the feed', () async {
      repo.cache.state = page(['cached']);
      final auth = FakeAuthService(signedIn: false);
      final c = await start(auth: auth);

      expect((await c.read(cachedPacketsProvider.future))?.packets, hasLength(1));
      expect(idsOf(c), ['cached']);
      expect(repo.cache.loadCalls, 1);

      // Signing in rebuilds the feed from the list it already holds.
      repo.onFetch = (_) async => page(['fresh']);
      await auth.signIn();
      await c.read(packetsProvider.future);
      await pumpEventQueue();

      expect(idsOf(c), ['fresh']);
      expect(repo.cache.loadCalls, 1);
    });

    test('is re-read, and the feed dropped, once the user data is cleared',
        () async {
      repo.onFetch = (_) async => page(['a']);
      final auth = FakeAuthService(signedIn: true);
      final c = await start(auth: auth);
      await pumpEventQueue();
      expect(cachedIds(), ['a']);

      // What AuthRepository does on sign-out, in this order.
      c.read(userDataGenerationProvider.notifier).state++;
      await repo.clearCache();
      await auth.signOut();
      await c.read(packetsProvider.future);

      expect(idsOf(c), isEmpty);
      expect(await c.read(cachedPacketsProvider.future), isNull);
      expect(repo.cache.loadCalls, 2);
      expect(repo.fetchCursors, [null]);
    });
  });

  group('PacketsNotifier rebuild', () {
    test('keeps the list in memory when the session expires', () async {
      repo.onFetch = (cursor) async =>
          cursor == null ? page(['a'], next: 'c1') : page(['b']);
      final auth = FakeAuthService(signedIn: true);
      final c = await start(auth: auth);
      final notifier = c.read(packetsProvider.notifier);
      await notifier.loadMore();
      await notifier.applyCreated(textPacket('live'));

      // The state flips without the data being cleared, as when a token
      // can't be refreshed.
      await auth.signOut();
      await c.read(packetsProvider.future);

      expect(idsOf(c), ['live', 'a', 'b']);
      expect(stateOf(c).hasMore, isFalse);
      expect(repo.cache.loadCalls, 1);
      expect(repo.fetchCursors, [null, 'c1']);
    });

    test('forgets a pending load-more error', () async {
      repo.onFetch = (cursor) async {
        if (cursor == null) return page(['a'], next: 'c1');
        throw Exception('offline');
      };
      final auth = FakeAuthService(signedIn: true);
      final c = await start(auth: auth);
      await c.read(packetsProvider.notifier).loadMore();
      expect(stateOf(c).loadMoreError, isNotNull);

      await auth.signOut();
      await c.read(packetsProvider.future);

      expect(idsOf(c), ['a']);
      expect(stateOf(c).loadMoreError, isNull);
      expect(stateOf(c).isLoadingMore, isFalse);
    });
  });

  group('PacketsNotifier cache writes', () {
    test('writes once for a burst of changes, after they settle', () {
      fakeAsync((async) {
        repo.onFetch = (_) async => page(['a']);
        final c = startInFakeAsync(async);
        final notifier = c.read(packetsProvider.notifier);
        expect(idsOf(c), ['a']);
        expect(repo.cache.saveCalls, 0);

        unawaited(notifier.applyCreated(textPacket('x')));
        unawaited(notifier.applyCreated(textPacket('y')));
        async.elapse(const Duration(milliseconds: 200));
        unawaited(notifier.applyCreated(textPacket('z')));
        async.elapse(const Duration(milliseconds: 200));

        // 400 ms since the first change but 200 ms since the last: not yet.
        expect(repo.cache.saveCalls, 0);

        async.elapse(const Duration(milliseconds: 100));
        expect(repo.cache.saveCalls, 1);
        expect(cachedIds(), ['z', 'y', 'x', 'a']);

        async.elapse(const Duration(seconds: 5));
        expect(repo.cache.saveCalls, 1);
        c.dispose();
      });
    });

    test('drops a write still pending when the user data is cleared', () {
      fakeAsync((async) {
        final auth = FakeAuthService(signedIn: true);
        repo.onFetch = (_) async => page(['a']);
        final c = startInFakeAsync(async, auth: auth);
        async.elapse(const Duration(seconds: 1));
        expect(repo.cache.saveCalls, 1);

        unawaited(c.read(packetsProvider.notifier).applyCreated(textPacket('x')));
        async.flushMicrotasks();
        expect(idsOf(c), ['x', 'a']);

        // Sign-out: the generation is bumped, the cache cleared, the state
        // flipped.  The write scheduled for `x` must not resurrect the cache.
        c.read(userDataGenerationProvider.notifier).state++;
        unawaited(repo.clearCache());
        unawaited(auth.signOut());
        async.elapse(const Duration(seconds: 1));

        expect(idsOf(c), isEmpty);
        expect(repo.cache.saveCalls, 1);
        expect(repo.cache.state, isNull);
        c.dispose();
      });
    });

    test('caches the first page and the cursor after it, not every page',
        () async {
      final first = ids('a', PacketCache.maxPackets);
      final second = ids('b', PacketCache.maxPackets);
      repo.onFetch = (cursor) async =>
          cursor == null ? page(first, next: 'c1') : page(second, next: 'c2');
      final c = await start();

      await c.read(packetsProvider.notifier).loadMore();
      await pumpEventQueue();

      expect(idsOf(c), [...first, ...second]);
      expect(stateOf(c).nextCursor, 'c2');
      expect(cachedIds(), first);
      expect(repo.cache.state?.nextCursor, 'c1');
    });

    test('keeps the first-page cursor when packets arrive on top', () async {
      repo.onFetch = (_) async => page(ids('a', PacketCache.maxPackets), next: 'c1');
      final c = await start();

      await c.read(packetsProvider.notifier).applyCreated(textPacket('live'));
      await pumpEventQueue();

      final cached = repo.cache.state!;
      expect(cached.packets, hasLength(PacketCache.maxPackets));
      expect(cached.packets.first.id, 'live');
      expect(cached.packets.last.id, 'a49');
      expect(cached.nextCursor, 'c1');
    });

    test('moves the first-page cursor on to the page a sync merged in',
        () async {
      var firstPageCalls = 0;
      repo.onFetch = (cursor) async {
        if (cursor == 'c1') return page(ids('b', 50), next: 'c2');
        return ++firstPageCalls == 1
            ? page(ids('a', 50), next: 'c1')
            : page(['new', ...ids('a', 49)], next: 'c1b');
      };
      final c = await start();
      final notifier = c.read(packetsProvider.notifier);
      await notifier.loadMore();

      await notifier.syncLatest();
      await pumpEventQueue();

      expect(idsOf(c), ['new', ...ids('a', 50), ...ids('b', 50)]);
      expect(cachedIds(), ['new', ...ids('a', 49)]);
      expect(repo.cache.state?.nextCursor, 'c1b');
    });

    test('takes the first-page cursor from the cache it started from',
        () async {
      repo.cache.state = page(ids('a', 50), next: 'c1');
      repo.onFetch = (_) async => throw Exception('offline');
      final c = await start();
      await pumpEventQueue();

      await c.read(packetsProvider.notifier).applyCreated(textPacket('live'));
      await pumpEventQueue();

      expect(cachedIds()?.first, 'live');
      expect(repo.cache.state?.packets, hasLength(50));
      expect(repo.cache.state?.nextCursor, 'c1');
    });

    test('saves a list that fits whole, with its own cursor', () async {
      repo.onFetch = (cursor) async =>
          cursor == null ? page(ids('a', 20), next: 'c1') : page(ids('b', 20));
      final c = await start();

      await c.read(packetsProvider.notifier).loadMore();
      await pumpEventQueue();

      expect(repo.cache.state?.packets, hasLength(40));
      expect(repo.cache.state?.nextCursor, isNull);
    });
  });
}
