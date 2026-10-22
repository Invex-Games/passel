import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/errors.dart';
import '../config/app_config.dart';
import 'auth_service.dart';

/// The sign-in button label: Google, or the development user about to sign
/// in (the typed [devUser], else the build's default).
String signInLabel([String devUser = '']) {
  if (!AppConfig.usesDevUser) return 'Sign in with Google';
  final user = devUser.trim().isEmpty ? AppConfig.devUser : devUser.trim();
  return 'Sign in as $user';
}

// ─── Full-screen login page (first launch, no cached data) ──────────────────

class LoginPage extends ConsumerStatefulWidget {
  const LoginPage({super.key});

  @override
  ConsumerState<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends ConsumerState<LoginPage> {
  bool _loading = false;
  String? _error;
  final _devUser = TextEditingController(text: AppConfig.devUser);

  @override
  void dispose() {
    _devUser.dispose();
    super.dispose();
  }

  Future<void> _signIn() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final success = await ref.read(authServiceProvider.notifier).signIn(
            devUser: AppConfig.usesDevUser ? _devUser.text : null,
          );
      if (!success && mounted) {
        setState(() => _error = 'Sign-in was cancelled.');
      }
    } catch (e) {
      if (mounted) setState(() => _error = describeError(e));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 32),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Icon(Icons.send_rounded, size: 72, color: cs.primary),
              const SizedBox(height: 24),
              Text(
                'Passel',
                style: Theme.of(context).textTheme.headlineMedium,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              Text(
                'Sign in to continue',
                style: Theme.of(context).textTheme.bodyMedium,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 40),
              if (AppConfig.usesDevUser) ...[
                DevUserField(
                  controller: _devUser,
                  enabled: !_loading,
                  onChanged: () => setState(() {}),
                  onSubmitted: _signIn,
                ),
                const SizedBox(height: 16),
              ],
              FilledButton.icon(
                onPressed: _loading ? null : _signIn,
                icon: _loading
                    ? const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.login),
                label: Text(
                  _loading ? 'Signing in…' : signInLabel(_devUser.text),
                ),
              ),
              if (_error != null) ...[
                const SizedBox(height: 16),
                Text(
                  _error!,
                  style: TextStyle(color: cs.error),
                  textAlign: TextAlign.center,
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

// ─── Development user field ─────────────────────────────────────────────────

/// Names the development user to sign in as; only built with `DEV_USER`.
///
/// Any name the API accepts works, and a different name is a different
/// account, which is how per-user behaviour gets tested on one device.
class DevUserField extends StatelessWidget {
  const DevUserField({
    super.key,
    required this.controller,
    required this.onChanged,
    required this.onSubmitted,
    this.enabled = true,
  });

  final TextEditingController controller;
  final VoidCallback onChanged;
  final VoidCallback onSubmitted;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      enabled: enabled,
      autocorrect: false,
      enableSuggestions: false,
      textInputAction: TextInputAction.go,
      decoration: const InputDecoration(
        labelText: 'Development user',
        helperText: 'Any name; a different name is a different account.',
        border: OutlineInputBorder(),
      ),
      onChanged: (_) => onChanged(),
      onSubmitted: (_) => onSubmitted(),
    );
  }
}

// ─── Bottom-sheet login (signed-out but has cached data) ────────────────────

/// Shows a modal bottom sheet prompting the user to sign in.
///
/// Automatically dismisses itself when sign-in succeeds.  The returned future
/// completes when the sheet is closed.
Future<void> showLoginSheet(BuildContext context) {
  return showModalBottomSheet<void>(
    context: context,
    // The development user field needs room above the keyboard.
    isScrollControlled: AppConfig.usesDevUser,
    builder: (_) => const _LoginSheet(),
  );
}

class _LoginSheet extends ConsumerStatefulWidget {
  const _LoginSheet();

  @override
  ConsumerState<_LoginSheet> createState() => _LoginSheetState();
}

class _LoginSheetState extends ConsumerState<_LoginSheet> {
  bool _loading = false;
  String? _error;
  final _devUser = TextEditingController(text: AppConfig.devUser);

  @override
  void dispose() {
    _devUser.dispose();
    super.dispose();
  }

  Future<void> _signIn() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final success = await ref.read(authServiceProvider.notifier).signIn(
            devUser: AppConfig.usesDevUser ? _devUser.text : null,
          );
      if (!success && mounted) {
        setState(() => _error = 'Sign-in was cancelled.');
      }
    } catch (e) {
      if (mounted) setState(() => _error = describeError(e));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    // Auto-dismiss on successful sign-in.
    ref.listen(authServiceProvider, (prev, next) {
      if (next.valueOrNull == true && mounted) {
        Navigator.of(context).pop();
      }
    });

    final cs = Theme.of(context).colorScheme;
    return SafeArea(
      child: Padding(
        padding: EdgeInsets.fromLTRB(
          24,
          12,
          24,
          24 + MediaQuery.of(context).viewInsets.bottom,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 32,
                height: 4,
                decoration: BoxDecoration(
                  color: cs.onSurfaceVariant.withValues(alpha: 0.4),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 20),
            Icon(Icons.lock_outline, size: 40, color: cs.primary),
            const SizedBox(height: 16),
            Text(
              'Sign in to sync',
              style: Theme.of(context).textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
            Text(
              'You can browse cached data offline, but sign in to refresh '
              'and push new packets.',
              style: Theme.of(context).textTheme.bodySmall,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 24),
            if (AppConfig.usesDevUser) ...[
              DevUserField(
                controller: _devUser,
                enabled: !_loading,
                onChanged: () => setState(() {}),
                onSubmitted: _signIn,
              ),
              const SizedBox(height: 16),
            ],
            FilledButton.icon(
              onPressed: _loading ? null : _signIn,
              icon: _loading
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.login),
              label: Text(
                _loading ? 'Signing in…' : signInLabel(_devUser.text),
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                style: TextStyle(color: cs.error, fontSize: 13),
                textAlign: TextAlign.center,
              ),
            ],
            const SizedBox(height: 8),
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Maybe later'),
            ),
          ],
        ),
      ),
    );
  }
}
