import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../auth/auth_service.dart';
import '../auth/data/account_info.dart';
import '../auth/login_page.dart';
import '../config/app_config.dart';
import 'app_version.dart';
import 'theme_mode_setting.dart';

/// The privacy policy and the terms of service on passel.link.
///
/// The web app has the pages (roadmap 1.E3), but nothing hosts them yet;
/// until something does, [legalPagesPublished] keeps the links disabled, so
/// nobody is sent to a page that is not there yet.
const String privacyPolicyUrl = 'https://passel.link/privacy';
const String termsUrl = 'https://passel.link/terms';
const bool legalPagesPublished = false;

/// Settings: who is signed in, the theme, and about the app.
///
/// Opened from the feed page's app bar.  Signed out it still offers the
/// theme and the version, and a way to sign in.
class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListView(
        children: const [
          _SectionHeader('Account'),
          _AccountSection(),
          _SectionHeader('Appearance'),
          _AppearanceSection(),
          _SectionHeader('About'),
          _AboutSection(),
          SizedBox(height: 24),
        ],
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader(this.title);

  final String title;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 20, 16, 4),
      child: Text(
        title,
        style: theme.textTheme.titleSmall
            ?.copyWith(color: theme.colorScheme.primary),
      ),
    );
  }
}

// ─── Account ─────────────────────────────────────────────────────────────────

/// Who is signed in, with Sign out; or Sign in, through the same sheet as
/// the feed.
class _AccountSection extends ConsumerWidget {
  const _AccountSection();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final signedIn = ref.watch(authServiceProvider).value == true;
    if (!signedIn) {
      return Column(
        children: [
          const ListTile(
            leading: CircleAvatar(child: Icon(Icons.person_outline)),
            title: Text('Not signed in'),
            subtitle: Text('Sign in to sync and send from this device'),
          ),
          ListTile(
            leading: const Icon(Icons.login),
            title: const Text('Sign in'),
            onTap: () => showLoginSheet(context),
          ),
        ],
      );
    }

    final auth = ref.read(authServiceProvider.notifier);
    final devUser = auth.currentDevUser;
    final account = auth.currentAccount;
    return Column(
      children: [
        if (devUser != null)
          ListTile(
            leading: const CircleAvatar(child: Icon(Icons.build_outlined)),
            title: Text(devUser),
            subtitle: const Text('Development user'),
          )
        else if (account != null)
          ListTile(
            leading: CircleAvatar(child: Text(_initial(account))),
            title: Text(account.title),
            subtitle: Text(
              account.displayName == null ? 'Google account' : account.email,
            ),
          )
        else
          // Signed in, but the account is not known: nothing to name.
          const ListTile(
            leading: CircleAvatar(child: Icon(Icons.person)),
            title: Text('Signed in'),
            subtitle: Text('Google account'),
          ),
        ListTile(
          leading: const Icon(Icons.logout),
          title: const Text('Sign out'),
          onTap: () => ref.read(authServiceProvider.notifier).signOut(),
        ),
      ],
    );
  }

  /// The first letter of the name, or of the email, for the avatar.
  static String _initial(AccountInfo account) {
    final title = account.title.trim();
    return title.isEmpty ? '?' : title.characters.first.toUpperCase();
  }
}

// ─── Appearance ──────────────────────────────────────────────────────────────

/// The theme, applied as it is chosen.
class _AppearanceSection extends ConsumerWidget {
  const _AppearanceSection();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(themeModeProvider);
    return Column(
      children: [
        ListTile(
          leading: const Icon(Icons.brightness_6_outlined),
          title: const Text('Theme'),
          subtitle: Text(describeThemeMode(mode)),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
          child: SegmentedButton<ThemeMode>(
            segments: const [
              ButtonSegment(
                value: ThemeMode.system,
                icon: Icon(Icons.brightness_auto_outlined),
                label: Text('System'),
              ),
              ButtonSegment(
                value: ThemeMode.light,
                icon: Icon(Icons.light_mode_outlined),
                label: Text('Light'),
              ),
              ButtonSegment(
                value: ThemeMode.dark,
                icon: Icon(Icons.dark_mode_outlined),
                label: Text('Dark'),
              ),
            ],
            selected: {mode},
            showSelectedIcon: false,
            expandedInsets: EdgeInsets.zero,
            onSelectionChanged: (selection) =>
                ref.read(themeModeProvider.notifier).set(selection.first),
          ),
        ),
      ],
    );
  }
}

// ─── About ───────────────────────────────────────────────────────────────────

/// The version; in a development build the flavor and the API too; and the
/// legal pages.
class _AboutSection extends ConsumerWidget {
  const _AboutSection();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final version = ref.watch(appVersionProvider);
    return Column(
      children: [
        ListTile(
          leading: const Icon(Icons.info_outline),
          title: const Text('Version'),
          subtitle: Text(switch (version) {
            AsyncData(:final value) => value.label,
            AsyncError() => 'Unknown',
            _ => '…',
          }),
        ),
        if (AppConfig.isDev) ...[
          ListTile(
            leading: const Icon(Icons.science_outlined),
            title: const Text('Flavor'),
            subtitle: Text(AppConfig.flavor),
          ),
          ListTile(
            leading: const Icon(Icons.dns_outlined),
            title: const Text('API'),
            subtitle: Text(
              AppConfig.apiBaseUrl.isEmpty ? 'Not set' : AppConfig.apiBaseUrl,
            ),
          ),
        ],
        const _LegalPageTile(
          icon: Icons.privacy_tip_outlined,
          title: 'Privacy policy',
          url: privacyPolicyUrl,
        ),
        const _LegalPageTile(
          icon: Icons.gavel_outlined,
          title: 'Terms of service',
          url: termsUrl,
        ),
      ],
    );
  }
}

/// A link to one of the legal pages, disabled until the page is published.
class _LegalPageTile extends StatelessWidget {
  const _LegalPageTile({
    required this.icon,
    required this.title,
    required this.url,
  });

  final IconData icon;
  final String title;
  final String url;

  @override
  Widget build(BuildContext context) {
    return ListTile(
      leading: Icon(icon),
      title: Text(title),
      subtitle: Text(
        legalPagesPublished
            ? url.replaceFirst(RegExp('^https?://'), '')
            : 'Not published yet',
      ),
      trailing: legalPagesPublished ? const Icon(Icons.open_in_new) : null,
      enabled: legalPagesPublished,
      onTap: () => _open(context),
    );
  }

  Future<void> _open(BuildContext context) async {
    var opened = false;
    try {
      opened = await launchUrl(
        Uri.parse(url),
        mode: LaunchMode.externalApplication,
      );
    } catch (_) {
      // Reported below.
    }
    if (!opened && context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text("Couldn't open the $title.")),
      );
    }
  }
}
