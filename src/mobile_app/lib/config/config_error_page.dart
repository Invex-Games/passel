import 'package:flutter/material.dart';

// ─── Configuration error page ────────────────────────────────────────────────

/// Shown instead of the app when the build-time configuration is unusable:
/// a required value is missing ([AppConfig.missingKeys]) or a value can't be
/// used in this flavor ([AppConfig.invalidValues]), rather than letting Google
/// Sign-In or the API client fail with a less obvious error.
class ConfigErrorPage extends StatelessWidget {
  const ConfigErrorPage({
    super.key,
    this.missingKeys = const [],
    this.invalidValues = const {},
  });

  /// Required values the build was compiled without.
  final List<String> missingKeys;

  /// Values the build can't use, keyed by name, with the reason.
  final Map<String, String> invalidValues;

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
                missingKeys.isNotEmpty
                    ? 'Missing configuration'
                    : 'Invalid configuration',
                style: textTheme.headlineSmall,
                textAlign: TextAlign.center,
              ),
              if (missingKeys.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text(
                  'This build was compiled without:',
                  style: textTheme.bodyMedium,
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 8),
                for (final key in missingKeys)
                  Text(key, style: mono, textAlign: TextAlign.center),
              ],
              if (invalidValues.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text(
                  'This build was compiled with values it can\'t use:',
                  style: textTheme.bodyMedium,
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 8),
                for (final entry in invalidValues.entries) ...[
                  Text(entry.key, style: mono, textAlign: TextAlign.center),
                  Text(
                    entry.value,
                    style: textTheme.bodySmall,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                ],
              ],
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
