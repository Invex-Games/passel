import 'package:custom_refresh_indicator/custom_refresh_indicator.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../api/errors.dart';
import '../../auth/auth_service.dart';
import '../../auth/login_page.dart';
import '../data/packet.dart';
import '../packets_provider.dart';
import 'animated_tile_wrapper.dart';
import 'delete_packet.dart';
import 'packet_tile.dart';
import 'refresh_overlay.dart';

/// Displays the packet list with reverse scrolling, infinite scroll for older
/// packets, and a bottom-pull-to-refresh using [CustomRefreshIndicator].
class PacketList extends ConsumerStatefulWidget {
  const PacketList({super.key});

  @override
  ConsumerState<PacketList> createState() => _PacketListState();
}

class _PacketListState extends ConsumerState<PacketList> {
  final _scrollController = ScrollController();

  /// Tracks packet IDs that have already animated in (don't re-animate).
  final _animatedIds = <String>{};

  /// Whether the scroll-triggered login prompt was already shown for the
  /// current signed-out period.  Prevents a new sheet on every scroll
  /// notification; reset when the auth state changes.
  bool _scrollLoginPromptShown = false;

  /// Whether a login sheet opened from this list is currently visible.
  bool _loginSheetOpen = false;

  @override
  void initState() {
    super.initState();
    // If packets are already cached in the provider, avoid replaying entrance
    // animations when this widget is recreated (prevents reload-like flicker).
    final cachedState = ref.read(packetsProvider).value;
    if (cachedState != null) {
      _animatedIds.addAll(cachedState.packets.map((packet) => packet.id));
    }
  }

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  Future<void> _promptLogin() async {
    if (_loginSheetOpen || !mounted) return;
    _loginSheetOpen = true;
    try {
      await showLoginSheet(context);
    } finally {
      _loginSheetOpen = false;
    }
  }

  void _checkLoadMore(ScrollNotification notification) {
    if (notification.metrics.pixels <
        notification.metrics.maxScrollExtent - 200) {
      return;
    }
    final packetsState = ref.read(packetsProvider).value;
    if (packetsState == null ||
        !packetsState.hasMore ||
        packetsState.isLoadingMore ||
        packetsState.loadMoreError != null) {
      return;
    }

    final isSignedIn = ref.read(authServiceProvider).value == true;
    if (!isSignedIn) {
      // Prompt at most once per signed-out period; the end-of-list row offers
      // a sign-in button after that.
      if (!_scrollLoginPromptShown) {
        _scrollLoginPromptShown = true;
        _promptLogin();
      }
      return;
    }

    _loadMore();
  }

  Future<void> _loadMore({bool retry = false}) async {
    final authed =
        await ref.read(packetsProvider.notifier).loadMore(retry: retry);
    if (!authed) _promptLogin();
  }

  Future<void> _onRefresh() async {
    _animatedIds.clear();
    final messenger = ScaffoldMessenger.maybeOf(context);
    try {
      final authed = await ref.read(packetsProvider.notifier).refresh();
      if (!authed) _promptLogin();
    } on SessionExpiredException {
      _promptLogin();
    } catch (e) {
      messenger?.showSnackBar(
        SnackBar(content: Text('Refresh failed: ${describeError(e)}')),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(packetsProvider);
    final isSignedIn = ref.watch(authServiceProvider).value == true;

    // A new auth state means a new chance to prompt on scroll.
    ref.listen(
      authServiceProvider.select((auth) => auth.value == true),
      (_, __) => _scrollLoginPromptShown = false,
    );

    final packetsState = asyncState.value;
    if (asyncState.isLoading && packetsState == null) {
      return const Center(child: CircularProgressIndicator());
    }

    if (asyncState.hasError && packetsState == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                describeError(asyncState.error!),
                textAlign: TextAlign.center,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
              const SizedBox(height: 16),
              FilledButton(
                onPressed: () => ref.invalidate(packetsProvider),
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      );
    }

    final packets = packetsState?.packets ?? [];

    // Lets the list find each existing tile's new position when packets are
    // inserted above it, so a live event moves the tiles instead of
    // rebuilding every visible one's state (animation, Dismissible).
    final indexById = <String, int>{
      for (var i = 0; i < packets.length; i++) packets[i].id: i,
    };

    return NotificationListener<ScrollNotification>(
      onNotification: (notification) {
        _checkLoadMore(notification);
        return false;
      },
      child: CustomRefreshIndicator(
        onRefresh: _onRefresh,
        trigger: IndicatorTrigger.leadingEdge,
        builder: (context, child, controller) {
          return Stack(
            clipBehavior: Clip.none,
            children: [
              child,
              RefreshOverlay(controller: controller),
            ],
          );
        },
        child: packets.isEmpty
            ? ListView(
                key: const PageStorageKey<String>('packet-list-view'),
                reverse: true,
                physics: const AlwaysScrollableScrollPhysics(),
                children: [
                  const SizedBox(height: 200),
                  Center(
                    child: !isSignedIn &&
                            packetsState?.lastSyncedAt == null
                        ? Column(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(
                                Icons.cloud_off,
                                size: 48,
                                color: Theme.of(context)
                                    .colorScheme
                                    .onSurface
                                    .withValues(alpha: 0.3),
                              ),
                              const SizedBox(height: 16),
                              const Text(
                                'Sign in to load your packets',
                                textAlign: TextAlign.center,
                              ),
                              const SizedBox(height: 12),
                              FilledButton.icon(
                                onPressed: _promptLogin,
                                icon: const Icon(Icons.login),
                                label: const Text('Sign in'),
                              ),
                            ],
                          )
                        : const Text(
                            // The list is reversed: its start, where the
                            // refresh gesture lives, is at the bottom.
                            'No packets yet.\nPull up to refresh.',
                            textAlign: TextAlign.center,
                          ),
                  ),
                ],
              )
            : ListView.separated(
                key: const PageStorageKey<String>('packet-list-view'),
                controller: _scrollController,
                reverse: true,
                physics: const AlwaysScrollableScrollPhysics(),
                // Bottom padding clears the FAB
                padding: const EdgeInsets.only(bottom: 124),
                // +1 for the top slot: "loading more" spinner, retry row,
                // sign-in prompt, or "all caught up".
                itemCount: packets.length + 1,
                // Tiles are keyed by packet id; the end-of-list slot has no
                // key.
                findItemIndexCallback: (key) =>
                    key is ValueKey<String> ? indexById[key.value] : null,
                separatorBuilder: (_, __) =>
                    const SizedBox(height: 8), // Space between packets
                itemBuilder: (context, index) {
                  if (index >= packets.length) {
                    return _buildEndOfList(context, packetsState, isSignedIn);
                  }
                  final packet = packets[index];
                  final shouldAnimate = _animatedIds.add(packet.id);
                  final tile = PacketTile(
                    packet: packet,
                    index: index,
                    onDelete: isSignedIn
                        ? () => deletePacketWithUndo(context, ref, packet)
                        : null,
                  );
                  return AnimatedTileWrapper(
                    key: ValueKey(packet.id),
                    animate: shouldAnimate,
                    staggerIndex: index,
                    child: isSignedIn
                        ? Dismissible(
                            key: ValueKey('dismiss-${packet.id}'),
                            direction: DismissDirection.endToStart,
                            background: _buildDismissBackground(context),
                            onDismissed: (_) =>
                                deletePacketWithUndo(context, ref, packet),
                            child: tile,
                          )
                        : tile,
                  );
                },
              ),
      ),
    );
  }

  /// Revealed behind a tile while it's swiped away to delete it.
  Widget _buildDismissBackground(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16),
      child: Container(
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.only(right: 24),
        decoration: BoxDecoration(
          color: cs.errorContainer,
          borderRadius: BorderRadius.circular(16),
        ),
        child: Icon(Icons.delete_outline, color: cs.onErrorContainer),
      ),
    );
  }

  /// The slot after the oldest packet (rendered at the top of the reversed
  /// list).
  Widget _buildEndOfList(
    BuildContext context,
    PacketsState? packetsState,
    bool isSignedIn,
  ) {
    final cs = Theme.of(context).colorScheme;
    final mutedStyle = Theme.of(context)
        .textTheme
        .bodyMedium
        ?.copyWith(color: cs.onSurface.withValues(alpha: 0.4));

    if (packetsState != null && packetsState.hasMore) {
      if (packetsState.loadMoreError != null && !packetsState.isLoadingMore) {
        return InkWell(
          onTap: () => _loadMore(retry: true),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(Icons.refresh, color: cs.error),
                const SizedBox(height: 8),
                Text(
                  'Couldn\'t load older packets. Tap to retry.',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: cs.error),
                ),
              ],
            ),
          ),
        );
      }

      if (!isSignedIn) {
        return Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                'Sign in to load older packets',
                textAlign: TextAlign.center,
                style: mutedStyle,
              ),
              const SizedBox(height: 8),
              TextButton.icon(
                onPressed: _promptLogin,
                icon: const Icon(Icons.login),
                label: const Text('Sign in'),
              ),
            ],
          ),
        );
      }

      return const Padding(
        padding: EdgeInsets.all(16),
        child: Center(child: CircularProgressIndicator()),
      );
    }

    return Padding(
      padding: const EdgeInsets.only(top: 16, bottom: 32, left: 24, right: 24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.list,
            size: 36,
            color: cs.onSurface.withValues(alpha: 0.3),
          ),
          const SizedBox(height: 10),
          Text(
            'You\'ve gone back to the beginning!',
            style: mutedStyle,
          ),
        ],
      ),
    );
  }
}
