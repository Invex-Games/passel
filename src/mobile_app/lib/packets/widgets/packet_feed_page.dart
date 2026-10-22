import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../auth/auth_service.dart';
import '../../auth/login_page.dart';
import '../../config/app_config.dart';
import '../../settings/settings_page.dart';
import '../../share/share_intent.dart';
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

  /// Text shared into the app while this page is shown.
  StreamSubscription<String>? _sharedText;

  /// Whether the send sheet is open for shared text, so a second share
  /// while it is up does not open another.
  bool _sendingShared = false;

  @override
  void initState() {
    super.initState();
    _syncLabelTimer = Timer.periodic(
      const Duration(seconds: 60),
      (_) {
        if (mounted) setState(() {});
      },
    );

    final shareIntent = ref.read(shareIntentProvider);
    _sharedText = shareIntent.sharedText.listen(_sendShared);
    // Text shared before the app was ready, once the page can show a sheet.
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      final text = await shareIntent.takePendingText();
      if (text != null && mounted) await _sendShared(text);
    });
  }

  @override
  void dispose() {
    _sharedText?.cancel();
    _syncLabelTimer?.cancel();
    super.dispose();
  }

  /// Opens the send sheet filled in with [text] shared from another app.
  Future<void> _sendShared(String text) async {
    if (_sendingShared || !mounted) return;
    _sendingShared = true;
    try {
      await showPushPacketSheet(context, ref, initialText: text);
    } finally {
      _sendingShared = false;
    }
  }

  /// How far the list must move one way before the app bar follows, so a
  /// wobble of the finger does not flip it.
  static const double _appBarScrollThreshold = 12;

  /// Distance scrolled in the current direction; restarts when it changes.
  double _scrollTravel = 0;

  /// Hides the app bar while the user scrolls down through older packets and
  /// shows it again on the way back up, so the feed gets the room.
  ///
  /// Decided from the distance the list actually moves, not from the drag
  /// direction: a pull to refresh at the top only overscrolls, and there the
  /// slightest finger movement flips the reported direction while nothing
  /// scrolls.
  bool _handleScrollNotification(ScrollNotification notification) {
    // Only the feed list itself, not a scrollable inside a tile.
    if (notification.depth != 0) return false;

    // At the top, the newest end, the bar always shows.
    if (notification.metrics.extentBefore == 0) {
      _scrollTravel = 0;
      _setAppBarShown(true);
      return false;
    }

    if (notification is ScrollUpdateNotification) {
      final delta = notification.scrollDelta ?? 0;
      if (delta == 0) return false;
      if (_scrollTravel.sign != delta.sign) _scrollTravel = 0;
      _scrollTravel += delta;
      // A positive delta scrolls down, toward the older packets.
      if (_scrollTravel.abs() >= _appBarScrollThreshold) {
        _setAppBarShown(_scrollTravel < 0);
      }
    }
    return false;
  }

  void _setAppBarShown(bool shown) {
    if (_showAppBar != shown) setState(() => _showAppBar = shown);
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
    final isSignedIn = ref.watch(authServiceProvider).value == true;
    final packetsState = ref.watch(packetsProvider).value;

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
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (isSignedIn)
                          IconButton(
                            icon: const Icon(Icons.logout),
                            tooltip: 'Sign out',
                            onPressed: () => ref
                                .read(authServiceProvider.notifier)
                                .signOut(),
                          )
                        else
                          IconButton(
                            icon: const Icon(Icons.login),
                            tooltip: 'Sign in',
                            onPressed: () => showLoginSheet(context),
                          ),
                        IconButton(
                          icon: const Icon(Icons.settings_outlined),
                          tooltip: 'Settings',
                          onPressed: () => Navigator.of(context).push(
                            MaterialPageRoute<void>(
                              builder: (_) => const SettingsPage(),
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            if (AppConfig.isDev)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  switch (ref.read(authServiceProvider.notifier).currentDevUser) {
                    final user? => '${AppConfig.packetsEndpoint} as $user',
                    null => AppConfig.packetsEndpoint,
                  },
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
              tooltip: 'Send',
              child: const Icon(Icons.add),
            )
          : null,
    );
  }
}
