import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../auth/auth_service.dart';
import '../../auth/login_page.dart';
import '../../config/app_config.dart';
import '../packets_provider.dart';
import 'packet_list.dart';
import 'push_packet_sheet.dart';

/// Top-level page showing the packet feed with a collapsible app bar and FAB.
class PacketFeedPage extends ConsumerStatefulWidget {
  const PacketFeedPage({super.key});

  @override
  ConsumerState<PacketFeedPage> createState() => _PacketFeedPageState();
}

class _PacketFeedPageState extends ConsumerState<PacketFeedPage> {
  bool _showAppBar = true;

  /// Timer that rebuilds the "Synced X ago" label every 60 seconds.
  Timer? _syncLabelTimer;

  @override
  void initState() {
    super.initState();
    _syncLabelTimer = Timer.periodic(
      const Duration(seconds: 60),
      (_) {
        if (mounted) setState(() {});
      },
    );
  }

  @override
  void dispose() {
    _syncLabelTimer?.cancel();
    super.dispose();
  }

  bool _handleScrollNotification(ScrollNotification notification) {
    if (notification is UserScrollNotification) {
      final direction = notification.direction;
      if (direction == ScrollDirection.reverse) {
        if (_showAppBar) setState(() => _showAppBar = false);
      } else if (direction == ScrollDirection.forward) {
        if (!_showAppBar) setState(() => _showAppBar = true);
      }
    }
    return false;
  }

  /// Formats [lastSyncedAt] as a relative "Synced X ago" string.
  String? _syncLabel(DateTime? lastSyncedAt) {
    if (lastSyncedAt == null) return null;
    final diff = DateTime.now().difference(lastSyncedAt);
    if (diff.inSeconds < 60) return 'Synced just now';
    if (diff.inMinutes < 60) {
      final m = diff.inMinutes;
      return 'Synced $m min${m == 1 ? '' : 's'} ago';
    }
    if (diff.inHours < 24) {
      final h = diff.inHours;
      return 'Synced $h hour${h == 1 ? '' : 's'} ago';
    }
    final d = diff.inDays;
    return 'Synced $d day${d == 1 ? '' : 's'} ago';
  }

  @override
  Widget build(BuildContext context) {
    final isSignedIn =
        ref.watch(authServiceProvider).valueOrNull == true;
    final packetsState = ref.watch(packetsProvider).valueOrNull;

    // Watching keeps the live connection open while the feed is shown; it
    // also reconnects and catches up whenever the app returns to the
    // foreground.
    final isLive = ref.watch(packetLiveUpdatesProvider);
    final syncText = isLive ? 'Live' : _syncLabel(packetsState?.lastSyncedAt);

    // No manual refresh on sign-in here: [PacketsNotifier] watches the auth
    // state and rebuilds (cache first, then a fresh fetch) when it changes.

    return Scaffold(
      body: NotificationListener<ScrollNotification>(
        onNotification: _handleScrollNotification,
        child: Column(
          children: [
            AnimatedContainer(
              duration: const Duration(milliseconds: 250),
              curve: Curves.easeOut,
              clipBehavior: Clip.antiAlias,
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.inversePrimary,
              ),
              height: _showAppBar
                  ? MediaQuery.of(context).padding.top + kToolbarHeight
                  : MediaQuery.of(context).padding.top,
              child: AppBar(
                backgroundColor: Theme.of(context).colorScheme.inversePrimary,
                title: AnimatedOpacity(
                  duration: const Duration(milliseconds: 200),
                  opacity: _showAppBar ? 1.0 : 0.0,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppConfig.isDev ? 'Passel (DEV)' : 'Passel'),
                      if (syncText != null)
                        Text(
                          syncText,
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: Theme.of(context)
                                    .colorScheme
                                    .onPrimaryContainer
                                    .withValues(alpha: 0.7),
                              ),
                        ),
                    ],
                  ),
                ),
                actions: [
                  AnimatedOpacity(
                    duration: const Duration(milliseconds: 200),
                    opacity: _showAppBar ? 1.0 : 0.0,
                    child: isSignedIn
                        ? IconButton(
                            icon: const Icon(Icons.logout),
                            tooltip: 'Sign out',
                            onPressed: () => ref
                                .read(authServiceProvider.notifier)
                                .signOut(),
                          )
                        : IconButton(
                            icon: const Icon(Icons.login),
                            tooltip: 'Sign in',
                            onPressed: () => showLoginSheet(context),
                          ),
                  ),
                ],
              ),
            ),
            if (AppConfig.isDev)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  AppConfig.packetsEndpoint,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            const Expanded(child: PacketList()),
          ],
        ),
      ),
      floatingActionButton: isSignedIn
          ? FloatingActionButton(
              onPressed: () => showPushPacketSheet(context, ref),
              tooltip: 'Push packet',
              child: const Icon(Icons.add),
            )
          : null,
    );
  }
}
