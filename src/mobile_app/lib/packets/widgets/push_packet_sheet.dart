import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../api/errors.dart';
import '../../auth/login_page.dart';
import '../data/link_utils.dart';
import '../packets_provider.dart';

/// Shows a bottom sheet for pushing a new text or link packet.
///
/// [context] must be the *page* context (not one inside the sheet): the
/// sheet is closed before the push runs, so feedback (failure snackbar, login
/// prompt) is shown from the page.
Future<void> showPushPacketSheet(BuildContext context, WidgetRef ref) async {
  // Capture page-level handles before opening the sheet.
  final messenger = ScaffoldMessenger.of(context);
  final notifier = ref.read(packetsProvider.notifier);

  final text = await showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    builder: (_) => const _PushPacketSheet(),
  );
  if (text == null || text.trim().isEmpty) return;

  final trimmed = text.trim();
  try {
    final authed = await notifier.push(
      type: packetTypeForInput(trimmed),
      payload: trimmed,
    );
    if (!authed && context.mounted) await showLoginSheet(context);
  } on SessionExpiredException {
    if (context.mounted) await showLoginSheet(context);
  } catch (e) {
    messenger.showSnackBar(
      SnackBar(content: Text('Send failed: ${describeError(e)}')),
    );
  }
}

/// Sheet content; pops with the entered text when the user submits.
class _PushPacketSheet extends StatefulWidget {
  const _PushPacketSheet();

  @override
  State<_PushPacketSheet> createState() => _PushPacketSheetState();
}

class _PushPacketSheetState extends State<_PushPacketSheet> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _submit() {
    final value = _controller.text;
    if (value.trim().isNotEmpty) Navigator.pop(context, value);
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.only(
        left: 16,
        right: 16,
        top: 24,
        bottom: MediaQuery.of(context).viewInsets.bottom + 16,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('Send a link or note', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 12),
          TextField(
            controller: _controller,
            autofocus: true,
            maxLines: 4,
            minLines: 1,
            textInputAction: TextInputAction.send,
            decoration: const InputDecoration(
              hintText: 'Type text or paste a URL…',
              border: OutlineInputBorder(),
            ),
            onSubmitted: (_) => _submit(),
          ),
          const SizedBox(height: 12),
          FilledButton.icon(
            icon: const Icon(Icons.send),
            label: const Text('Send'),
            onPressed: _submit,
          ),
        ],
      ),
    );
  }
}
