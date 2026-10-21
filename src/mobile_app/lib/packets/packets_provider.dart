import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

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

  @override
  Future<PacketsState> build() async {
    // Rebuild from scratch whenever the user signs in / out or the account
    // changes, so the feed never shows another session's data.
    final signedIn = ref.watch(_isSignedInProvider);
    _epoch++;
    _generation++;
    final seq = ++_replaceSeq;

    // Always serve cached data first so the feed appears instantly.
    final cached = await _repo.loadCached();

    // If signed in, try to fetch fresh data in the background.
    if (signedIn) {
      if (cached != null) {
        // Return cache immediately, then silently refresh.
        unawaited(_backgroundRefresh());
        return cached;
      }
      // No cache — must fetch from network.
      final fresh = await _repo.fetchPackets();
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
    final fresh = await _repo.fetchPackets();
    if (seq != _replaceSeq) return;

    _generation++;
    state = AsyncData(fresh);
    await _persist(fresh);
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

    final updated = latest.copyWith(
      packets: mergePackets([created], latest.packets),
    );
    state = AsyncData(updated);
    await _persist(updated);
    return true;
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
