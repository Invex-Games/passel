import 'package:dynamic_color/dynamic_color.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'api/errors.dart';
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
          // Without a usable configuration the app can't sign in or reach the
          // API, so explain how to fix the build instead of failing later.
          home: AppConfig.isValid
              ? const StartupRouter()
              : ConfigErrorPage(
                  missingKeys: AppConfig.missingKeys,
                  invalidValues: AppConfig.invalidValues,
                ),
        );
      },
    );
  }
}

// ─── Startup router ──────────────────────────────────────────────────────────

/// Routes to [LoginPage] (signed-out, no cache) or [PacketFeedPage]
/// (signed-in, OR signed-out with cached data to browse offline).
///
/// A cached feed is shown as soon as the cache has been read, without
/// waiting for Google Sign-In to initialise (a network round-trip on a cold
/// start): the feed page treats auth that is still initialising as signed
/// out and updates when it settles.  Without a cache the decision needs the
/// auth state, so the spinner stays until it is known.
///
/// Whether there is a feed to show comes from [cachedPacketsProvider] until
/// the feed has loaded, and from the feed itself after that, so the decision
/// stays reactive without re-reading the disk: an explicit sign-out (which
/// clears the cache and the feed) returns to [LoginPage], while an expired
/// session keeps the cached feed visible with a sign-in prompt (via
/// [showLoginSheet]).  Because none of these providers depend on the widget
/// tree, a [DynamicColorBuilder] theme rebuild does *not* re-trigger auth.
class StartupRouter extends ConsumerWidget {
  const StartupRouter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authServiceProvider);
    final cacheState = ref.watch(cachedPacketsProvider);
    final feedHasPackets = ref.watch(packetsProvider.select(
      (feed) => feed.hasValue ? feed.requireValue.packets.isNotEmpty : null,
    ));

    // `null` while the cache is being read for the first time.  (Re-checks
    // keep the previous value, so the current page stays mounted instead of
    // flashing a spinner.)
    final bool? hasCache;
    if (feedHasPackets != null) {
      hasCache = feedHasPackets;
    } else if (cacheState.hasValue || cacheState.hasError) {
      hasCache = cacheState.valueOrNull?.packets.isNotEmpty ?? false;
    } else {
      hasCache = null;
    }

    // Cached data can be browsed signed out, so don't wait for auth.
    if (hasCache == true) return const PacketFeedPage();

    // Reading the cache, or no cache and auth still initializing — spinner.
    final authPending = !authState.hasValue && !authState.hasError;
    if (hasCache == null || authPending) {
      return const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
    }

    // Auth init error — surface it.
    if (authState.hasError) {
      return Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Text(
              describeError(authState.error!),
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    // Signed out AND no cached data → full-screen login; otherwise the feed.
    return authState.valueOrNull == true
        ? const PacketFeedPage()
        : const LoginPage();
  }
}
