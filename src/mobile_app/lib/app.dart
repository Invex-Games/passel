import 'package:dynamic_color/dynamic_color.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'auth/auth_service.dart';
import 'auth/login_page.dart';
import 'packets/packets_provider.dart';
import 'packets/widgets/packet_feed_page.dart';
import 'theme/theme.dart';

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    final textTheme = Theme.of(context).textTheme;
    final fallbackTheme = MaterialTheme(textTheme);

    return DynamicColorBuilder(
      builder: (ColorScheme? lightDynamic, ColorScheme? darkDynamic) {
        return MaterialApp(
          title: 'Passel',
          theme: lightDynamic != null
              ? fallbackTheme.theme(lightDynamic)
              : fallbackTheme.light(),
          darkTheme: darkDynamic != null
              ? fallbackTheme.theme(darkDynamic)
              : fallbackTheme.dark(),
          home: const StartupRouter(),
        );
      },
    );
  }
}

// ─── Startup router ──────────────────────────────────────────────────────────

/// Routes to [LoginPage] (signed-out, no cache) or [PacketFeedPage]
/// (signed-in, OR signed-out with cached data to browse offline).
///
/// Sits inside [MaterialApp.home] but only makes a routing decision once during
/// cold start — after that, auth changes are handled by the feed page itself
/// (via [showLoginSheet]).  Because neither [authServiceProvider] nor
/// [hasCachedPacketsProvider] depend on the widget tree, a
/// [DynamicColorBuilder] theme rebuild does *not* re-trigger auth.
class StartupRouter extends ConsumerWidget {
  const StartupRouter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authServiceProvider);
    final cacheState = ref.watch(hasCachedPacketsProvider);

    // Still initializing auth or checking cache — show spinner.
    if (authState.isLoading || cacheState.isLoading) {
      return const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
    }

    // Auth init error — surface it.
    if (authState.hasError) {
      return Scaffold(
        body: Center(child: Text('Auth error: ${authState.error}')),
      );
    }

    final isSignedIn = authState.valueOrNull == true;
    final hasCache = cacheState.valueOrNull == true;

    // Signed out AND no cached data → full-screen login.
    if (!isSignedIn && !hasCache) {
      return const LoginPage();
    }

    // Otherwise go straight to the feed (works both online and offline).
    return const PacketFeedPage();
  }
}
