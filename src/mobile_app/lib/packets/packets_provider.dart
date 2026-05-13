import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_helpers.dart';
import '../auth/auth_service.dart';
import 'data/packet.dart';
import 'data/packet_cache.dart';
import 'data/packet_repository.dart';

// ─── Repository provider ─────────────────────────────────────────────────────

final packetRepositoryProvider = Provider<PacketRepository>((ref) {
  return PacketRepository(
    client: ref.read(apiClientProvider),
    tokenProvider: () => requireToken(ref),
  );
});

// ─── Lightweight cache-check provider (startup routing only) ─────────────────

/// Returns `true` if the on-disk packet cache contains data.
///
/// Used by [StartupRouter] to decide between the full-screen [LoginPage]
/// (no cache) and [PacketFeedPage] (has cache, can browse offline).
final hasCachedPacketsProvider = FutureProvider<bool>((ref) async {
  final cached = await PacketCache.load();
  return cached != null && cached.packets.isNotEmpty;
});

// ─── Packets provider ────────────────────────────────────────────────────────

final packetsProvider =
    AsyncNotifierProvider<PacketsNotifier, PacketsState>(PacketsNotifier.new);

class PacketsNotifier extends AsyncNotifier<PacketsState> {
  PacketRepository get _repo => ref.read(packetRepositoryProvider);

  bool get _isSignedIn =>
      ref.read(authServiceProvider).valueOrNull == true;

  @override
  Future<PacketsState> build() async {
    // Always serve cached data first so the feed appears instantly.
    final cached = await _repo.loadCached();

    // If signed in, try to fetch fresh data in the background.
    if (_isSignedIn) {
      if (cached != null) {
        // Return cache immediately, then silently refresh.
        _backgroundRefresh();
        return cached;
      }
      // No cache — must fetch from network.
      return _repo.fetchPackets();
    }

    // Signed out — return whatever we have cached (may be empty).
    return cached ?? const PacketsState();
  }

  /// Fetches fresh data without flipping state to [AsyncLoading].
  Future<void> _backgroundRefresh() async {
    try {
      final fresh = await _repo.fetchPackets();
      state = AsyncData(fresh);
    } catch (_) {
      // Network failure during background refresh — keep showing cache.
    }
  }

  /// Loads the next page and appends to the current list.
  ///
  /// Returns `false` if the user is signed out (caller should prompt sign-in).
  Future<bool> loadMore() async {
    if (!_isSignedIn) return false;

    final current = state.valueOrNull;
    if (current == null || !current.hasMore || current.isLoadingMore) {
      return true;
    }

    state = AsyncData(current.copyWith(isLoadingMore: true));

    try {
      final nextPage = await _repo.fetchPackets(cursor: current.nextCursor);
      final merged = PacketsState(
        packets: [...current.packets, ...nextPage.packets],
        nextCursor: nextPage.nextCursor,
        lastSyncedAt: nextPage.lastSyncedAt,
      );
      await _repo.saveCache(merged);
      state = AsyncData(merged);
    } catch (_) {
      // Revert loading flag but keep existing data visible.
      state = AsyncData(current.copyWith(isLoadingMore: false));
    }
    return true;
  }

  /// Push a new packet (text or link) then refresh the list.
  ///
  /// Returns `false` if the user is signed out (caller should prompt sign-in).
  Future<bool> push({
    required String type,
    required String payload,
    String? title,
  }) async {
    if (!_isSignedIn) return false;

    final refreshed = await _repo.pushPacket(
      type: type,
      payload: payload,
      title: title,
    );
    // Refresh without going through AsyncLoading.
    state = AsyncData(refreshed);
    return true;
  }

  /// Manually refresh the packets list (pull-to-refresh).
  ///
  /// Returns `false` if the user is signed out (caller should prompt sign-in).
  Future<bool> refresh() async {
    if (!_isSignedIn) return false;

    // Fetch fresh data and assign directly — keeps existing data visible
    // during the network request (no AsyncLoading flash).
    state = AsyncData(await _repo.fetchPackets());
    return true;
  }
}
