import 'package:flutter/material.dart';

// ─── Missing configuration page ──────────────────────────────────────────────

/// Shown instead of the app when required build-time configuration is
/// missing (see [AppConfig.missingKeys]), rather than letting Google Sign-In
/// or the API client fail with a less obvious error.
class ConfigErrorPage extends StatelessWidget {
  const ConfigErrorPage({super.key, required this.missingKeys});

  final List<String> missingKeys;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final textTheme = Theme.of(context).textTheme;
    final mono = textTheme.bodyMedium?.copyWith(fontFamily: 'monospace');

    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 32),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Icon(Icons.settings_suggest_outlined, size: 72, color: cs.error),
              const SizedBox(height: 24),
              Text(
                'Missing configuration',
                style: textTheme.headlineSmall,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 16),
              Text(
                'This build was compiled without:',
                style: textTheme.bodyMedium,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              for (final key in missingKeys)
                Text(key, style: mono, textAlign: TextAlign.center),
              const SizedBox(height: 16),
              Text(
                'Copy .env.example to .env, fill it in, and run the app with '
                'the "Mobile app (development)" launch configuration, or:',
                style: textTheme.bodyMedium,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              SelectableText(
                'flutter run --flavor development --dart-define-from-file=.env',
                style: mono,
                textAlign: TextAlign.center,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
