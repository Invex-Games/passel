import 'dart:async';
import 'dart:math';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../app_lifecycle.dart';
import '../auth/auth_helpers.dart';
import '../auth/auth_service.dart';
import 'data/packet.dart';
import 'data/packet_repository.dart';

// ─── Repository provider ─────────────────────────────────────────────────────

final packetRepositoryProvider = Provider<PacketRepository>((ref) {
  return PacketRepository(
    client: ref.read(apiClientProvider),
    tokenProvider: ({bool forceRefresh = false}) =>
        requireToken(ref, forceRefresh: forceRefresh),
  );
});

/// Whether the user is currently signed in (`false` while auth initializes).
///
/// Per-user providers watch this rather than [authServiceProvider] directly
/// so they only rebuild when the signed-in flag actually flips.
final _isSignedInProvider = Provider<bool>((ref) {
  return ref.watch(authServiceProvider.select((auth) => auth.valueOrNull == true));
});

// ─── Lightweight cache-check provider (startup routing only) ─────────────────

/// Returns `true` if the on-disk packet cache contains data.
///
/// Used by [StartupRouter] to decide between the full-screen [LoginPage]
/// (signed out, no cache) and [PacketFeedPage] (has cache, can browse
/// offline).  Re-evaluated whenever the signed-in state changes (sign-out and
/// account changes clear the cache) and whenever [PacketsNotifier] saves.
final hasCachedPacketsProvider = FutureProvider<bool>((ref) async {
  ref.watch(_isSignedInProvider);
  final cached = await ref.read(packetRepositoryProvider).loadCached();
  return cached != null && cached.packets.isNotEmpty;
});

// ─── Packets provider ────────────────────────────────────────────────────────

final packetsProvider =
    AsyncNotifierProvider<PacketsNotifier, PacketsState>(PacketsNotifier.new);

class PacketsNotifier extends AsyncNotifier<PacketsState> {
  PacketRepository get _repo => ref.read(packetRepositoryProvider);

  bool get _isSignedIn => ref.read(authServiceProvider).valueOrNull == true;

  /// Bumped on every [build] (i.e. auth change).  Results of requests started
  /// under an older epoch belong to a previous session and are dropped.
  int _epoch = 0;

  /// Bumped whenever the list is replaced wholesale (build, refresh,
  /// background refresh).  A [loadMore] that started under an older
  /// generation is discarded so a stale page is never merged into a newer
  /// list (which could leave gaps or duplicates).
  int _generation = 0;

  /// Sequence number of the latest replace request; an older replace that
  /// completes late is dropped instead of overwriting newer data.
  int _replaceSeq = 0;

  /// Packets added outside a fetch (pushed, or received from the live
  /// stream) since the last [build], oldest first.  A fetch that was already
  /// in flight when one arrived may predate it, so the ones that arrived
  /// during the fetch are merged back on top of the fetched page.
  final _arrivals = <Packet>[];

  @override
  Future<PacketsState> build() async {
    // Rebuild from scratch whenever the user signs in / out or the account
    // changes, so the feed never shows another session's data.
    final signedIn = ref.watch(_isSignedInProvider);
    _epoch++;
    _generation++;
    _arrivals.clear();
    final seq = ++_replaceSeq;

    // Always serve cached data first so the feed appears instantly.
    final cached = await _repo.loadCached();

    // If signed in, try to fetch fresh data in the background.
    if (signedIn) {
      if (cached != null) {
        // Return cache immediately, then silently refresh.
        unawaited(_backgroundRefresh());
        return _withArrivalsSince(0, cached);
      }
      // No cache — must fetch from network.
      final fresh = _withArrivalsSince(0, await _repo.fetchPackets());
      if (seq == _replaceSeq) unawaited(_persist(fresh));
      return fresh;
    }

    // Signed out — return whatever we have cached (may be empty).
    return cached ?? const PacketsState();
  }

  /// Fetches fresh data without flipping state to [AsyncLoading].
  Future<void> _backgroundRefresh() async {
    try {
      await _replaceFromNetwork();
    } catch (_) {
      // Network failure during background refresh — keep showing cache.
    }
  }

  /// Fetches the first page and replaces the list with it, unless a newer
  /// replace (or a rebuild) started in the meantime.
  Future<void> _replaceFromNetwork() async {
    final seq = ++_replaceSeq;
    final arrivalsMark = _arrivals.length;
    final fresh = await _repo.fetchPackets();
    if (seq != _replaceSeq) return;

    _generation++;
    final replaced = _withArrivalsSince(arrivalsMark, fresh);
    state = AsyncData(replaced);
    await _persist(replaced);
  }

  /// Returns [page] with the packets that arrived since [arrivalsMark] (an
  /// earlier `_arrivals.length`) merged on top, newest first.
  PacketsState _withArrivalsSince(int arrivalsMark, PacketsState page) {
    if (arrivalsMark >= _arrivals.length) return page;
    final arrived = _arrivals.sublist(arrivalsMark).reversed.toList();
    return page.copyWith(packets: mergePackets(arrived, page.packets));
  }

  /// Saves [packetsState] to disk; failures are non-fatal.
  Future<void> _persist(PacketsState packetsState) async {
    try {
      await _repo.saveCache(packetsState);
    } catch (_) {}
    ref.invalidate(hasCachedPacketsProvider);
  }

  /// Loads the next page and appends it to the current list.
  ///
  /// Does nothing while a previous load-more error is pending unless [retry]
  /// is `true` (the user tapped "retry"), so scrolling doesn't hammer a
  /// failing endpoint.
  ///
  /// Returns `false` if the user is signed out or the session expired
  /// (caller should prompt sign-in).
  Future<bool> loadMore({bool retry = false}) async {
    if (!_isSignedIn) return false;

    final current = state.valueOrNull;
    if (current == null || !current.hasMore || current.isLoadingMore) {
      return true;
    }
    if (current.loadMoreError != null && !retry) return true;

    final generation = _generation;
    final cursor = current.nextCursor;
    state = AsyncData(
      current.copyWith(isLoadingMore: true, loadMoreError: () => null),
    );

    try {
      final nextPage = await _repo.fetchPackets(cursor: cursor);
      // The list was replaced while we were loading — this page is stale.
      if (generation != _generation) return true;

      final latest = state.valueOrNull ?? current;
      final merged = latest.copyWith(
        packets: mergePackets(latest.packets, nextPage.packets),
        // Defensive: a server echoing the same cursor would loop forever.
        nextCursor: () =>
            nextPage.nextCursor == cursor ? null : nextPage.nextCursor,
        isLoadingMore: false,
      );
      state = AsyncData(merged);
      await _persist(merged);
      return true;
    } catch (e) {
      if (generation == _generation) {
        final latest = state.valueOrNull ?? current;
        // Keep existing data visible; remember the error so the list shows
        // a "tap to retry" row instead of retrying on every scroll.
        state = AsyncData(
          latest.copyWith(isLoadingMore: false, loadMoreError: () => e),
        );
      }
      return e is! SessionExpiredException;
    }
  }

  /// Pushes a new text or link packet and prepends it to the list.
  ///
  /// Returns `false` if the user is signed out (caller should prompt sign-in).
  /// Throws if the push fails.
  Future<bool> push({
    required PacketType type,
    required String payload,
    String? title,
  }) async {
    if (!_isSignedIn) return false;

    final epoch = _epoch;
    final created = await _repo.pushPacket(
      type: type,
      payload: payload,
      title: title,
    );
    // Signed out / switched account while pushing — don't touch the new
    // session's data.
    if (epoch != _epoch) return true;

    final latest = state.valueOrNull;
    if (created == null || latest == null) {
      // Can't merge locally (unparseable response or no list yet) — fetch.
      await _replaceFromNetwork();
      return true;
    }

    _arrivals.add(created);
    final updated = latest.copyWith(
      packets: mergePackets([created], latest.packets),
    );
    state = AsyncData(updated);
    await _persist(updated);
    return true;
  }

  /// Adds a packet received from the live stream to the top of the list.
  Future<void> applyCreated(Packet packet) async {
    if (!_isSignedIn) return;
    _arrivals.add(packet);

    // While (re)building, the build merges [_arrivals] into its result.
    if (state.isLoading) return;
    final latest = state.valueOrNull;
    if (latest == null) return;

    // Usually this device's own push, echoed back.
    if (latest.packets.any((p) => p.id == packet.id)) return;

    final updated = latest.copyWith(
      packets: mergePackets([packet], latest.packets),
    );
    state = AsyncData(updated);
    await _persist(updated);
  }

  /// Fetches the first page and merges it into the top of the list, keeping
  /// any older pages that were already loaded.
  ///
  /// Used to catch up when the live stream (re)connects.  If the page
  /// doesn't overlap the list, too much changed to stitch them together and
  /// the list is replaced instead.  Throws if the request fails.
  Future<void> syncLatest() async {
    if (!_isSignedIn) return;

    if (state.isLoading) {
      try {
        // The build's own fetch may predate the caller's reason to sync.
        await future;
      } catch (_) {
        return;
      }
    }

    final seq = ++_replaceSeq;
    final arrivalsMark = _arrivals.length;
    final fresh = await _repo.fetchPackets();
    if (seq != _replaceSeq) return;

    final page = _withArrivalsSince(arrivalsMark, fresh);
    final latest = state.valueOrNull;
    final listedIds = {...?latest?.packets.map((p) => p.id)};

    final PacketsState synced;
    if (latest == null || !page.packets.any((p) => listedIds.contains(p.id))) {
      _generation++;
      synced = page;
    } else {
      synced = latest.copyWith(
        packets: mergePackets(page.packets, latest.packets),
        lastSyncedAt: () => fresh.lastSyncedAt,
      );
    }
    state = AsyncData(synced);
    await _persist(synced);
  }

  /// Manually refresh the packets list (pull-to-refresh).
  ///
  /// Keeps existing data visible during the request (no AsyncLoading flash).
  /// Returns `false` if the user is signed out (caller should prompt sign-in).
  /// Throws if the request fails ([SessionExpiredException] if the session
  /// could not be refreshed).
  Future<bool> refresh() async {
    if (!_isSignedIn) return false;
    await _replaceFromNetwork();
    return true;
  }
}

// ─── Live updates provider ───────────────────────────────────────────────────

/// Keeps [packetsProvider] up to date by listening to the server's packet
/// event stream; `true` while connected.
///
/// The stream is open only while this provider is watched (by
/// [PacketFeedPage]), the user is signed in, and the app is in the
/// foreground.  Coming back to the foreground reconnects, and every
/// (re)connection catches up on what was missed with
/// [PacketsNotifier.syncLatest].
final packetLiveUpdatesProvider =
    NotifierProvider.autoDispose<PacketLiveUpdates, bool>(
        PacketLiveUpdates.new);

class PacketLiveUpdates extends AutoDisposeNotifier<bool> {
  /// Upper bound for the delay between reconnection attempts.
  static const maxRetryDelay = Duration(minutes: 1);

  final _random = Random();

  StreamSubscription<PacketEvent>? _connection;
  Timer? _retryTimer;

  /// Consecutive connection failures, for the retry backoff.
  int _failures = 0;

  /// Whether the feed hasn't caught up since the stream was (re)started.
  /// Normally the stream's first resync does it; if connecting fails, the
  /// feed is synced directly instead (so it still refreshes on resume when
  /// the stream is unavailable).
  bool _needsSync = true;

  PacketsNotifier get _packets => ref.read(packetsProvider.notifier);

  @override
  bool build() {
    final active =
        ref.watch(_isSignedInProvider) && ref.watch(appForegroundProvider);
    ref.onDispose(_stop);

    _failures = 0;
    _needsSync = true;
    if (active) _connect();
    return false;
  }

  void _connect() {
    _retryTimer = null;
    late final StreamSubscription<PacketEvent> connection;
    connection = ref.read(packetRepositoryProvider).watchEvents().listen(
          (event) => _onEvent(connection, event),
          onError: (Object error) => _onDisconnected(connection, error),
          onDone: () => _onDisconnected(connection, null),
          cancelOnError: true,
        );
    _connection = connection;
  }

  void _onEvent(StreamSubscription<PacketEvent> connection, PacketEvent event) {
    switch (event) {
      case PacketsResyncEvent():
        _failures = 0;
        _needsSync = false;
        state = true;
        unawaited(_sync(connection));
      case PacketCreatedEvent(:final packet):
        unawaited(_packets.applyCreated(packet));
    }
  }

  /// Catches up after a resync.  If that fails, packets may have been
  /// missed, so the connection is dropped to retry the whole handshake.
  Future<void> _sync(StreamSubscription<PacketEvent> connection) async {
    try {
      await _packets.syncLatest();
    } catch (e) {
      if (!identical(connection, _connection)) return;
      unawaited(connection.cancel());
      _onDisconnected(connection, e);
    }
  }

  void _onDisconnected(
    StreamSubscription<PacketEvent> connection,
    Object? error,
  ) {
    // A connection that was already replaced or stopped.
    if (!identical(connection, _connection)) return;
    _connection = null;
    state = false;

    if (_needsSync) {
      _needsSync = false;
      unawaited(_packets.syncLatest().catchError((_) {}));
    }

    // The auth state flips to signed-out (or the user is prompted on their
    // next action); reconnecting with the same session would fail again.
    if (error is SessionExpiredException) return;

    _retryTimer = Timer(_retryDelay(_failures++), _connect);
  }

  /// Exponential backoff from 1 second up to [maxRetryDelay], with jitter so
  /// clients don't all reconnect at once after a server restart.
  Duration _retryDelay(int failures) {
    final base = Duration(seconds: 1 << min(failures, 6));
    final capped = base < maxRetryDelay ? base : maxRetryDelay;
    return capped * (0.75 + _random.nextDouble() * 0.25);
  }

  void _stop() {
    _retryTimer?.cancel();
    _retryTimer = null;
    final connection = _connection;
    _connection = null;
    unawaited(connection?.cancel());
  }
}
