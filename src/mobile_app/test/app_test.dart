import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/app.dart';
import 'package:passel_mobileapp/app_lifecycle.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/auth/login_page.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/packet_feed_page.dart';

import 'fakes.dart';

void main() {
  late FakePacketRepository repo;

  Future<void> pumpRouter(
    WidgetTester tester, {
    required AuthService Function() auth,
  }) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(auth),
        packetRepositoryProvider.overrideWithValue(repo),
        appForegroundProvider.overrideWith(() => FakeAppForeground()),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: StartupRouter()),
    ));
  }

  /// Unmounts the pages, which stops the feed page's periodic timer.
  Future<void> tearDownWidgets(WidgetTester tester) =>
      tester.pumpWidget(const SizedBox.shrink());

  PacketsState feed(List<String> ids) => PacketsState(
        packets: ids.map(textPacket).toList(),
        lastSyncedAt: DateTime(2026, 10, 21),
      );

  setUp(() => repo = FakePacketRepository());

  group('StartupRouter', () {
    testWidgets('shows the cached feed before sign-in has initialised',
        (tester) async {
      repo.cache.state = feed(['a']);
      final auth = PendingAuthService();
      await pumpRouter(tester, auth: () => auth);
      await tester.pumpAndSettle();

      expect(find.byType(PacketFeedPage), findsOneWidget);
      expect(find.text('body a'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsNothing);
      expect(repo.fetchCursors, isEmpty);

      // Auth settles signed in: the feed stays put and refreshes.
      repo.onFetch = (_) async => feed(['fresh', 'a']);
      auth.complete(true);
      await tester.pumpAndSettle();

      expect(find.byType(PacketFeedPage), findsOneWidget);
      expect(find.text('body fresh'), findsOneWidget);
      expect(repo.fetchCursors, [null]);
      await tearDownWidgets(tester);
    });

    testWidgets('waits for sign-in without a cache, then shows the login page',
        (tester) async {
      final auth = PendingAuthService();
      await pumpRouter(tester, auth: () => auth);
      // Frames, not pumpAndSettle: the spinner animates forever.
      await tester.pump();
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byType(PacketFeedPage), findsNothing);

      auth.complete(false);
      await tester.pumpAndSettle();

      expect(find.byType(LoginPage), findsOneWidget);
      await tearDownWidgets(tester);
    });

    testWidgets('shows the feed when signed in without a cache',
        (tester) async {
      repo.onFetch = (_) async => feed(['a']);
      await pumpRouter(tester, auth: () => FakeAuthService(signedIn: true));
      await tester.pumpAndSettle();

      expect(find.byType(PacketFeedPage), findsOneWidget);
      expect(find.text('body a'), findsOneWidget);
      await tearDownWidgets(tester);
    });

    testWidgets('keeps the feed when the session expires', (tester) async {
      repo.onFetch = (_) async => feed(['a']);
      final auth = FakeAuthService(signedIn: true);
      await pumpRouter(tester, auth: () => auth);
      await tester.pumpAndSettle();

      await auth.signOut();
      await tester.pumpAndSettle();

      expect(find.byType(PacketFeedPage), findsOneWidget);
      expect(find.text('body a'), findsOneWidget);
      await tearDownWidgets(tester);
    });

    testWidgets('returns to the login page once sign-out clears the cache',
        (tester) async {
      repo.onFetch = (_) async => feed(['a']);
      final auth = FakeAuthService(signedIn: true);
      await pumpRouter(tester, auth: () => auth);
      await tester.pumpAndSettle();
      expect(find.byType(PacketFeedPage), findsOneWidget);

      // What AuthRepository does on sign-out, in this order.
      final container = ProviderScope.containerOf(
        tester.element(find.byType(StartupRouter)),
      );
      container.read(userDataGenerationProvider.notifier).bump();
      await repo.clearCache();
      await auth.signOut();
      await tester.pumpAndSettle();

      expect(find.byType(LoginPage), findsOneWidget);
      expect(find.byType(PacketFeedPage), findsNothing);
      await tearDownWidgets(tester);
    });
  });
}
