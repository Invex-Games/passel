import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_feed_page.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';
import 'package:passel_mobileapp/settings/settings_page.dart';

import '../fakes.dart';

/// The feed page signed in and out: its actions, the FAB and the sync label.
void main() {
  late FakePacketRepository repo;
  late FakeAuthService auth;

  Future<void> pumpFeed(WidgetTester tester, {bool signedIn = true}) async {
    auth = FakeAuthService(signedIn: signedIn);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => auth),
        packetRepositoryProvider.overrideWithValue(repo),
        appForegroundProvider.overrideWith(() => FakeAppForeground()),
        // Exact reconnect delays, so a dropped stream retries after 1 s.
        packetLiveUpdatesProvider
            .overrideWith(() => PacketLiveUpdates(random: FakeRandom())),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: PacketFeedPage()),
    ));
    await tester.pumpAndSettle();
  }

  PacketsState feed({DateTime? syncedAt}) =>
      PacketsState(packets: [textPacket('a')], lastSyncedAt: syncedAt);

  final fab = find.byType(FloatingActionButton);
  final signIn = find.byTooltip('Sign in');
  final signOut = find.byTooltip('Sign out');
  final settings = find.byTooltip('Settings');

  setUp(() {
    repo = FakePacketRepository()..onFetch = (_) async => feed();
  });

  group('signed in', () {
    testWidgets('shows the feed with a sign-out action and a Send button',
        (tester) async {
      await pumpFeed(tester);

      expect(find.byType(PacketList), findsOneWidget);
      expect(find.text('body a'), findsOneWidget);
      expect(signOut, findsOneWidget);
      expect(signIn, findsNothing);
      expect(fab, findsOneWidget);
      expect(find.byTooltip('Send'), findsOneWidget);
    });

    testWidgets('the Send button opens the send sheet', (tester) async {
      await pumpFeed(tester);

      await tester.tap(fab);
      await tester.pumpAndSettle();

      expect(find.text('Send a link or note'), findsOneWidget);
    });

    testWidgets('signing out asks the service and keeps the feed readable',
        (tester) async {
      await pumpFeed(tester);

      await tester.tap(signOut);
      await tester.pumpAndSettle();

      expect(auth.signOutCalls, 1);
      expect(fab, findsNothing);
      expect(signOut, findsNothing);
      expect(signIn, findsOneWidget);
      expect(find.text('body a'), findsOneWidget);
    });
  });

  group('signed out', () {
    testWidgets('offers sign-in instead of sending', (tester) async {
      repo.cache.state = feed(syncedAt: DateTime(2026, 10, 21));
      await pumpFeed(tester, signedIn: false);

      expect(find.text('body a'), findsOneWidget);
      expect(fab, findsNothing);
      expect(signOut, findsNothing);
      expect(signIn, findsOneWidget);
      expect(repo.fetchCursors, isEmpty);
    });

    testWidgets('the sign-in action opens the sign-in sheet, and signing in '
        'brings the Send button back', (tester) async {
      repo.cache.state = feed(syncedAt: DateTime(2026, 10, 21));
      await pumpFeed(tester, signedIn: false);

      await tester.tap(signIn);
      await tester.pumpAndSettle();
      expect(find.text('Sign in to sync'), findsOneWidget);

      await tester.tap(find.text('Sign in with Google'));
      await tester.pumpAndSettle();

      expect(auth.signInCalls, 1);
      expect(find.text('Sign in to sync'), findsNothing);
      expect(fab, findsOneWidget);
      expect(signOut, findsOneWidget);
    });
  });

  group('the sync label', () {
    testWidgets('says Live while the stream is up, and when it last synced '
        'otherwise', (tester) async {
      final syncedAt = DateTime.now().subtract(const Duration(minutes: 5));
      repo.onFetch = (_) async => feed(syncedAt: syncedAt);
      await pumpFeed(tester);

      // The stream is open but the server has not confirmed it yet.
      expect(find.text('Synced 5 mins ago'), findsOneWidget);
      expect(find.text('Live'), findsNothing);

      final connection = repo.connections.single;
      connection.add(const PacketsResyncEvent());
      await tester.pumpAndSettle();
      expect(find.text('Live'), findsOneWidget);
      expect(find.textContaining('Synced'), findsNothing);

      await connection.close();
      await tester.pumpAndSettle();
      expect(find.text('Live'), findsNothing);
      expect(find.text('Synced 5 mins ago'), findsOneWidget);
    });

    testWidgets('counts seconds, minutes, hours and days', (tester) async {
      const cases = [
        (Duration(seconds: 10), 'Synced just now'),
        (Duration(minutes: 1, seconds: 5), 'Synced 1 min ago'),
        (Duration(minutes: 12), 'Synced 12 mins ago'),
        (Duration(hours: 1, minutes: 5), 'Synced 1 hour ago'),
        (Duration(hours: 2, minutes: 5), 'Synced 2 hours ago'),
        (Duration(days: 1, hours: 1), 'Synced 1 day ago'),
        (Duration(days: 3, hours: 1), 'Synced 3 days ago'),
      ];
      for (final (age, label) in cases) {
        repo.onFetch = (_) async => feed(syncedAt: DateTime.now().subtract(age));
        await pumpFeed(tester);

        expect(find.text(label), findsOneWidget, reason: 'synced $age ago');

        // A fresh page next time: a ProviderScope cannot change overrides.
        await tester.pumpWidget(const SizedBox.shrink());
      }
    });

    testWidgets('is absent before the first sync', (tester) async {
      await pumpFeed(tester);

      expect(find.textContaining('Synced'), findsNothing);
      expect(find.text('Live'), findsNothing);
    });
  });

  group('the Settings action', () {
    testWidgets('opens the settings page, which mirrors Sign out',
        (tester) async {
      await pumpFeed(tester);
      expect(settings, findsOneWidget);

      await tester.tap(settings);
      await tester.pumpAndSettle();

      expect(find.byType(SettingsPage), findsOneWidget);
      expect(find.text('Sign out'), findsOneWidget);

      await tester.tap(find.text('Sign out'));
      await tester.pumpAndSettle();

      expect(auth.signOutCalls, 1);
      expect(find.text('Not signed in'), findsOneWidget);
    });

    testWidgets('is there signed out too', (tester) async {
      repo.cache.state = feed(syncedAt: DateTime(2026, 10, 21));
      await pumpFeed(tester, signedIn: false);

      await tester.tap(settings);
      await tester.pumpAndSettle();

      expect(find.byType(SettingsPage), findsOneWidget);
      expect(find.text('Not signed in'), findsOneWidget);
      expect(find.text('Sign in'), findsOneWidget);
    });
  });
}
