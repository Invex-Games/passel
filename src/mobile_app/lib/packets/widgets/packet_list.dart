import 'package:custom_refresh_indicator/custom_refresh_indicator.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../auth/auth_service.dart';
import '../../auth/login_page.dart';
import '../packets_provider.dart';
import 'animated_tile_wrapper.dart';
import 'packet_tile.dart';
import 'refresh_overlay.dart';

/// Displays the packet list with reverse scrolling, infinite scroll for older
/// packets, and a bottom-pull-to-refresh using [CustomRefreshIndicator].
class PacketList extends ConsumerStatefulWidget {
  const PacketList({super.key});

  @override
  ConsumerState<PacketList> createState() => _PacketListState();
}

class _PacketListState extends ConsumerState<PacketList>
    with AutomaticKeepAliveClientMixin<PacketList> {
  final _scrollController = ScrollController();

  /// Tracks packet IDs that have already animated in (don't re-animate).
  final _animatedIds = <String>{};

  @override
  void initState() {
    super.initState();
    // If packets are already cached in the provider, avoid replaying entrance
    // animations when this widget is recreated (prevents reload-like flicker).
    final cachedState = ref.read(packetsProvider).valueOrNull;
    if (cachedState != null) {
      _animatedIds.addAll(cachedState.packets.map((packet) => packet.id));
    }
  }

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  void _checkLoadMore(ScrollNotification notification) {
    if (notification.metrics.pixels >=
        notification.metrics.maxScrollExtent - 200) {
      final packetsState = ref.read(packetsProvider).valueOrNull;
      if (packetsState != null &&
          packetsState.hasMore &&
          !packetsState.isLoadingMore) {
        ref.read(packetsProvider.notifier).loadMore().then((authed) {
          if (!authed && mounted) showLoginSheet(context);
        });
      }
    }
  }

  Future<void> _onRefresh() async {
    _animatedIds.clear();
    final authed = await ref.read(packetsProvider.notifier).refresh();
    if (!authed && mounted) showLoginSheet(context);
  }

  @override
  bool get wantKeepAlive => true;

  @override
  Widget build(BuildContext context) {
    super.build(context);
    final asyncState = ref.watch(packetsProvider);
    final isSignedIn =
        ref.watch(authServiceProvider).valueOrNull == true;

    final packetsState = asyncState.valueOrNull;
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
                asyncState.error.toString(),
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
    final hasMore = packetsState?.hasMore ?? false;

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
                                onPressed: () => showLoginSheet(context),
                                icon: const Icon(Icons.login),
                                label: const Text('Sign in'),
                              ),
                            ],
                          )
                        : const Text(
                            'No packets yet.\nPull down to refresh.',
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
                // +1 for the top slot: "loading more" spinner or "all caught up"
                itemCount: packets.length + 1,
                separatorBuilder: (_, __) =>
                    const SizedBox(height: 8), // Space between packets
                itemBuilder: (context, index) {
                  if (index >= packets.length) {
                    if (hasMore) {
                      return const Padding(
                        padding: EdgeInsets.all(16),
                        child: Center(child: CircularProgressIndicator()),
                      );
                    }
                    return Padding(
                      padding: const EdgeInsets.only(
                          top: 16, bottom: 32, left: 24, right: 24),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(
                            Icons.list,
                            size: 36,
                            color: Theme.of(context)
                                .colorScheme
                                .onSurface
                                .withValues(alpha: 0.3),
                          ),
                          const SizedBox(height: 10),
                          Text(
                            'You\'ve gone back to the beginning!',
                            style: Theme.of(context)
                                .textTheme
                                .bodyMedium
                                ?.copyWith(
                                  color: Theme.of(context)
                                      .colorScheme
                                      .onSurface
                                      .withValues(alpha: 0.4),
                                ),
                          ),
                        ],
                      ),
                    );
                  }
                  final packet = packets[index];
                  final shouldAnimate = _animatedIds.add(packet.id);
                  return AnimatedTileWrapper(
                    key: ValueKey(packet.id),
                    animate: shouldAnimate,
                    staggerIndex: index,
                    child: PacketTile(
                      packet: packet,
                      index: index,
                    ),
                  );
                },
              ),
      ),
    );
  }
}
