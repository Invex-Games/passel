import 'dart:async';
import 'dart:math';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/errors.dart';
import '../app_lifecycle.dart';
import '../auth/auth_helpers.dart';
import '../auth/auth_service.dart';
import '../provider_retry.dart';
import 'data/packet.dart';
import 'data/packet_cache.dart';
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
  return ref.watch(authServiceProvider.select((auth) => auth.value == true));
});

// ─── Cached packets ──────────────────────────────────────────────────────────

/// The feed as last saved to disk, parsed once at start-up.
///
/// [StartupRouter] reads it to show the feed before Google Sign-In has
/// initialised, and [PacketsNotifier] serves it before refreshing, so the
/// cache is parsed once rather than once per reader.  It is only re-read
/// after the signed-in user's data was cleared (sign-out, account change),
/// which is cheap because there is nothing left to parse; from then on the
/// notifier keeps the list in memory and never needs the disk again.
final cachedPacketsProvider = FutureProvider<PacketsState?>((ref) {
  ref.watch(userDataGenerationProvider);
  return ref.read(packetRepositoryProvider).loadCached();
}, retry: noRetry);

/// How long [PacketsNotifier] waits after a change before writing the cache,
/// so a burst of changes (a resync, a run of live events, a delete and its
/// echo) is written once.  Tests override it with [Duration.zero].
final packetCacheWriteDelayProvider =
    Provider<Duration>((_) => const Duration(milliseconds: 300));

// ─── Packets provider ────────────────────────────────────────────────────────

final packetsProvider = AsyncNotifierProvider<PacketsNotifier, PacketsState>(
  PacketsNotifier.new,
  retry: noRetry,
);

class PacketsNotifier extends AsyncNotifier<PacketsState> {
  PacketRepository get _repo => ref.read(packetRepositoryProvider);

  bool get _isSignedIn => ref.read(authServiceProvider).value == true;

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

  /// Ids of packets deleted since the last [build], including ones only
  /// hidden while their deletion can be undone.  A fetch may have been
  /// served before the deletion, so these are removed from anything fetched.
  final _deletedIds = <String>{};

  /// Packets hidden by [hide] and where they were shown (index, and the ids
  /// of the packets either side), so [unhide] can put them back.
  final _hidden = <String,
      ({Packet packet, int index, String? newerId, String? olderId})>{};

  /// The cursor the server returned with the first page the list is built
  /// on, i.e. where the second page starts.  The cache holds the first page
  /// only, so this (rather than [PacketsState.nextCursor], which follows the
  /// last page loaded) is the cursor saved with it.  Remembered explicitly
  /// whenever the list is replaced or synced; cursors are opaque, so it can't
  /// be derived from the packets.
  String? _firstPageCursor;

  /// The [userDataGenerationProvider] value the list was built under.  A
  /// newer one means the user's data was cleared since, so the list in memory
  /// belongs to a previous session and must be neither kept nor written.
  int? _userDataGeneration;

  Duration _writeDelay = Duration.zero;
  Timer? _writeTimer;
  PacketsState? _pendingWrite;

  @override
  Future<PacketsState> build() async {
    // Rebuild from scratch whenever the user signs in / out or the account
    // changes, so the feed never shows another session's data.
    final signedIn = ref.watch(_isSignedInProvider);
    _epoch++;
    _generation++;
    _arrivals.clear();
    _deletedIds.clear();
    _hidden.clear();
    final seq = ++_replaceSeq;
    _writeDelay = ref.read(packetCacheWriteDelayProvider);

    // A pending cache write must not land after the cache was cleared: the
    // generation is bumped before the clear and before the auth state flips,
    // so dropping it here is in time.
    ref.listen(userDataGenerationProvider, (_, __) => _dropWrite());

    // The list already in memory survives a rebuild (sign-in from the cached
    // feed, an expired session) unless the user's data was cleared meanwhile:
    // it is at least as fresh as the disk, which is never re-read.
    final userData = ref.read(userDataGenerationProvider);
    PacketsState? kept;
    if (userData == _userDataGeneration) {
      _flushWrite(_repo);
      final previous = state.value;
      if (previous != null && previous.packets.isNotEmpty) {
        kept = previous.copyWith(isLoadingMore: false, loadMoreError: () => null);
      }
    } else {
      _userDataGeneration = userData;
      _dropWrite();
    }

    // Always serve cached data first so the feed appears instantly.
    final cached = kept ?? await _loadCached();

    // If signed in, try to fetch fresh data in the background.
    if (signedIn) {
      if (cached != null) {
        // Return cache immediately, then silently refresh.
        unawaited(_backgroundRefresh());
        return _reconcile(0, cached);
      }
      // No cache — must fetch from network.
      final fresh = _reconcile(0, await _repo.fetchPackets());
      if (seq == _replaceSeq) {
        _firstPageCursor = fresh.nextCursor;
        _persist(fresh);
      }
      return fresh;
    }

    // Signed out — return whatever we have cached (may be empty).
    return cached ?? const PacketsState();
  }

  /// The cache parsed at start-up (see [cachedPacketsProvider]); it holds a
  /// first page, so its cursor is the first-page cursor.
  Future<PacketsState?> _loadCached() async {
    final cached = await ref.read(cachedPacketsProvider.future);
    if (cached != null) _firstPageCursor = cached.nextCursor;
    return cached;
  }

  /// Fetches fresh data without flipping state to [AsyncLoading].
  ///
  /// Started from [build], so it first lets the build's own result land: a
  /// state set while the build is still pending is overwritten when the
  /// build completes.
  Future<void> _backgroundRefresh() async {
    final epoch = _epoch;
    try {
      await future;
    } catch (_) {
      return;
    }
    if (epoch != _epoch) return;

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
    _firstPageCursor = fresh.nextCursor;
    final replaced = _reconcile(arrivalsMark, fresh);
    state = AsyncData(replaced);
    _persist(replaced);
  }

  /// Brings a fetched [page] up to date with what happened while it was in
  /// flight: the packets that arrived since [arrivalsMark] (an earlier
  /// `_arrivals.length`) are merged on top, newest first, and deleted
  /// packets are removed.
  PacketsState _reconcile(int arrivalsMark, PacketsState page) {
    final arrived = arrivalsMark < _arrivals.length
        ? _arrivals.sublist(arrivalsMark).reversed.toList()
        : const <Packet>[];
    return page.copyWith(
      packets: _withoutDeleted(mergePackets(arrived, page.packets)),
    );
  }

  List<Packet> _withoutDeleted(List<Packet> packets) {
    if (_deletedIds.isEmpty) return packets;
    return List.unmodifiable(
        packets.where((p) => !_deletedIds.contains(p.id)));
  }

  // ── Cache writes ───────────────────────────────────────────────────────────

  /// Saves the first page of [packetsState] to disk once things settle: the
  /// write is delayed by [packetCacheWriteDelayProvider] and a newer call
  /// replaces it, so a burst of changes is written once, with the latest
  /// list.  Failures are non-fatal.
  void _persist(PacketsState packetsState) {
    _pendingWrite = packetsState;
    _writeTimer?.cancel();
    // Captured now: when the timer fires, `ref` may be unusable (between a
    // dependency change and the rebuild, or after disposal).
    final repo = _repo;
    _writeTimer = Timer(_writeDelay, () => _flushWrite(repo));
  }

  /// Writes the pending snapshot now, if there is one.
  void _flushWrite(PacketRepository repo) {
    _writeTimer?.cancel();
    _writeTimer = null;
    final pending = _pendingWrite;
    _pendingWrite = null;
    if (pending == null) return;
    unawaited(_write(repo, pending));
  }

  void _dropWrite() {
    _writeTimer?.cancel();
    _writeTimer = null;
    _pendingWrite = null;
  }

  Future<void> _write(PacketRepository repo, PacketsState packetsState) async {
    try {
      await repo.saveCache(_cacheSnapshot(packetsState));
    } catch (_) {}
  }

  /// The part of [packetsState] worth caching: the first page (at most
  /// [PacketCache.maxPackets] packets) and the cursor to fetch the rest with.
  ///
  /// The cache exists to show something instantly on the next start; older
  /// pages are fetched again when the user scrolls to them.  A list that fits
  /// is saved whole, with its own cursor.
  PacketsState _cacheSnapshot(PacketsState packetsState) {
    final packets = packetsState.packets;
    if (packets.length <= PacketCache.maxPackets) return packetsState;
    return packetsState.copyWith(
      packets: List.unmodifiable(packets.sublist(0, PacketCache.maxPackets)),
      nextCursor: () => _firstPageCursor,
    );
  }

  // ── Loading and changes ────────────────────────────────────────────────────

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

    final current = state.value;
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

      final latest = state.value ?? current;
      final merged = latest.copyWith(
        packets:
            mergePackets(latest.packets, _withoutDeleted(nextPage.packets)),
        // Defensive: a server echoing the same cursor would loop forever.
        nextCursor: () =>
            nextPage.nextCursor == cursor ? null : nextPage.nextCursor,
        isLoadingMore: false,
      );
      state = AsyncData(merged);
      _persist(merged);
      return true;
    } catch (e) {
      if (generation == _generation) {
        final latest = state.value ?? current;
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

    final latest = state.value;
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
    _persist(updated);
    return true;
  }

  /// Adds a packet received from the live stream to the top of the list.
  Future<void> applyCreated(Packet packet) async {
    if (!_isSignedIn || _deletedIds.contains(packet.id)) return;
    _arrivals.add(packet);

    // While (re)building, the build merges [_arrivals] into its result.
    if (state.isLoading) return;
    final latest = state.value;
    if (latest == null) return;

    // Usually this device's own push, echoed back.
    if (latest.packets.any((p) => p.id == packet.id)) return;

    final updated = latest.copyWith(
      packets: mergePackets([packet], latest.packets),
    );
    state = AsyncData(updated);
    _persist(updated);
  }

  /// Removes a packet deleted on another device (or echoed back after this
  /// device deleted it).
  Future<void> applyDeleted(String id) async {
    if (!_isSignedIn) return;
    _deletedIds.add(id);
    // It's gone for good: an undo can't bring it back.
    _hidden.remove(id);

    // While (re)building, the build filters [_deletedIds] out of its result.
    if (state.isLoading) return;
    final latest = state.value;
    if (latest == null || !latest.packets.any((p) => p.id == id)) return;

    final updated = latest.copyWith(
      packets: List.unmodifiable(latest.packets.where((p) => p.id != id)),
    );
    state = AsyncData(updated);
    _persist(updated);
  }

  /// Removes a packet from the list without deleting it yet, so the deletion
  /// can still be undone with [unhide].  Commit it with [delete].
  ///
  /// Returns `false` if the packet isn't listed.
  bool hide(String id) {
    final latest = state.value;
    final index = latest?.packets.indexWhere((p) => p.id == id) ?? -1;
    if (latest == null || index == -1) return false;

    final packets = latest.packets;
    _hidden[id] = (
      packet: packets[index],
      index: index,
      newerId: index > 0 ? packets[index - 1].id : null,
      olderId: index < packets.length - 1 ? packets[index + 1].id : null,
    );
    _deletedIds.add(id);
    final updated = latest.copyWith(
      packets: List.unmodifiable(latest.packets.where((p) => p.id != id)),
    );
    state = AsyncData(updated);
    _persist(updated);
    return true;
  }

  /// Puts a packet hidden by [hide] back where it was.
  void unhide(String id) {
    final hidden = _hidden.remove(id);
    // Already deleted (here or on another device).
    if (hidden == null) return;
    _deletedIds.remove(id);

    final latest = state.value;
    if (latest == null || latest.packets.any((p) => p.id == id)) return;

    // Packets may have arrived or been loaded since, so prefer going back
    // next to the same neighbours over the same index.
    final ids = latest.packets.map((p) => p.id).toList();
    final olderIndex = ids.indexOf(hidden.olderId ?? '');
    final newerIndex = ids.indexOf(hidden.newerId ?? '');
    final index = olderIndex != -1
        ? olderIndex
        : newerIndex != -1
            ? newerIndex + 1
            : min(hidden.index, ids.length);

    final packets = [...latest.packets]..insert(index, hidden.packet);
    final updated = latest.copyWith(packets: List.unmodifiable(packets));
    state = AsyncData(updated);
    _persist(updated);
  }

  /// Deletes a packet on the server, hiding it first if it's still listed.
  ///
  /// Returns `false` (and puts the packet back) if the user is signed out
  /// (caller should prompt sign-in).  If the request fails the packet is
  /// put back and the error rethrown.
  Future<bool> delete(String id) async {
    hide(id);
    if (!_isSignedIn) {
      unhide(id);
      return false;
    }

    final epoch = _epoch;
    _deletedIds.add(id);
    try {
      await _repo.deletePacket(id);
    } catch (_) {
      if (epoch == _epoch) {
        if (_hidden.containsKey(id)) {
          unhide(id);
        } else {
          _deletedIds.remove(id);
        }
      }
      rethrow;
    }
    if (epoch == _epoch) _hidden.remove(id);
    return true;
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

    final page = _reconcile(arrivalsMark, fresh);
    final latest = state.value;
    final listedIds = {...?latest?.packets.map((p) => p.id)};

    // Either way the list now starts with this page, so the cached first
    // page continues from its cursor.
    _firstPageCursor = fresh.nextCursor;
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
    _persist(synced);
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
///
/// Many clients lose the server at the same moment (a restart, its
/// database connection dropping), so both the reconnect delay and the
/// catch-up fetch are spread out at random; only the first catch-up after
/// the stream (re)starts is immediate, so opening or returning to the app
/// shows fresh data straight away.
final packetLiveUpdatesProvider =
    NotifierProvider.autoDispose<PacketLiveUpdates, bool>(
        PacketLiveUpdates.new);

class PacketLiveUpdates extends Notifier<bool> {
  /// [random] drives the jitter; tests pass a scripted one.
  PacketLiveUpdates({Random? random}) : _random = random ?? Random();

  /// Upper bound for the delay between reconnection attempts.
  static const maxRetryDelay = Duration(minutes: 1);

  /// Upper bound for the delay before the fetch a later resync triggers.
  static const maxResyncDelay = Duration(seconds: 3);

  final Random _random;

  StreamSubscription<PacketEvent>? _connection;
  Timer? _retryTimer;
  Timer? _syncTimer;

  /// Consecutive connection failures, for the retry backoff.
  int _failures = 0;

  /// Whether the feed hasn't caught up since the stream was (re)started.
  /// Normally the stream's first resync does it; if connecting fails, the
  /// feed is synced directly instead (so it still refreshes on resume when
  /// the stream is unavailable).
  bool _needsSync = true;

  /// Whether a resync has arrived since the stream was (re)started.  The
  /// first is acted on at once; later ones (the server restarted or lost
  /// its own listener, and told every client at the same time) are delayed
  /// by up to [maxResyncDelay] so the fetches don't all land together.
  bool _resynced = false;

  PacketsNotifier get _packets => ref.read(packetsProvider.notifier);

  @override
  bool build() {
    final active =
        ref.watch(_isSignedInProvider) && ref.watch(appForegroundProvider);
    ref.onDispose(_stop);

    _failures = 0;
    _needsSync = true;
    _resynced = false;
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
        _scheduleSync(connection);
      case PacketCreatedEvent(:final packet):
        unawaited(_packets.applyCreated(packet));
      case PacketDeletedEvent(:final id):
        unawaited(_packets.applyDeleted(id));
    }
  }

  /// Catches up after a resync: at once for the first one since the stream
  /// (re)started, otherwise after a random share of [maxResyncDelay].
  void _scheduleSync(StreamSubscription<PacketEvent> connection) {
    _syncTimer?.cancel();
    _syncTimer = null;
    if (!_resynced) {
      _resynced = true;
      unawaited(_sync(connection));
      return;
    }
    _syncTimer = Timer(maxResyncDelay * _random.nextDouble(), () {
      _syncTimer = null;
      unawaited(_sync(connection));
    });
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

  /// Exponential backoff from 1 second up to [maxRetryDelay], with full
  /// jitter: anywhere from zero up to the backoff, so clients that lost the
  /// server at the same moment spread their reconnects over the whole
  /// window instead of bunching at the end of it.
  Duration _retryDelay(int failures) {
    final base = Duration(seconds: 1 << min(failures, 6));
    final capped = base < maxRetryDelay ? base : maxRetryDelay;
    return capped * _random.nextDouble();
  }

  void _stop() {
    _retryTimer?.cancel();
    _retryTimer = null;
    _syncTimer?.cancel();
    _syncTimer = null;
    final connection = _connection;
    _connection = null;
    unawaited(connection?.cancel());
  }
}
