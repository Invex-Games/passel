import 'package:dynamic_color/dynamic_color.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'auth/auth_service.dart';
import 'auth/login_page.dart';
import 'config/app_config.dart';
import 'config/config_error_page.dart';
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
          // Without its configuration the app can't sign in or reach the API,
          // so explain how to fix the build instead of failing later.
          home: AppConfig.missingKeys.isNotEmpty
              ? ConfigErrorPage(missingKeys: AppConfig.missingKeys)
              : const StartupRouter(),
        );
      },
    );
  }
}

// ─── Startup router ──────────────────────────────────────────────────────────

/// Routes to [LoginPage] (signed-out, no cache) or [PacketFeedPage]
/// (signed-in, OR signed-out with cached data to browse offline).
///
/// The decision is reactive: [hasCachedPacketsProvider] is re-evaluated on
/// every auth change and cache save, so e.g. an explicit sign-out (which
/// clears the cache) returns to [LoginPage], while an expired session keeps
/// the cached feed visible with a sign-in prompt (via [showLoginSheet]).
/// Because neither [authServiceProvider] nor [hasCachedPacketsProvider]
/// depend on the widget tree, a [DynamicColorBuilder] theme rebuild does
/// *not* re-trigger auth.
class StartupRouter extends ConsumerWidget {
  const StartupRouter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authServiceProvider);
    final cacheState = ref.watch(hasCachedPacketsProvider);

    // Still initializing auth or checking cache for the first time — show
    // spinner.  (Re-checks keep the previous value, so the current page stays
    // mounted instead of flashing a spinner.)
    final authPending = !authState.hasValue && !authState.hasError;
    final cachePending = !cacheState.hasValue && !cacheState.hasError;
    if (authPending || cachePending) {
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
