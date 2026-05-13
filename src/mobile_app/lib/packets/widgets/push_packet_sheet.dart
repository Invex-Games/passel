import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../auth/login_page.dart';
import '../packets_provider.dart';

/// Shows a bottom sheet for pushing a new text or link packet.
void showPushPacketSheet(BuildContext context, WidgetRef ref) {
  final controller = TextEditingController();
  showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    builder: (context) => Padding(
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
          Text('Push a packet', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 12),
          TextField(
            controller: controller,
            autofocus: true,
            maxLines: 4,
            minLines: 1,
            textInputAction: TextInputAction.send,
            decoration: const InputDecoration(
              hintText: 'Type text or paste a URL…',
              border: OutlineInputBorder(),
            ),
            onSubmitted: (value) {
              if (value.trim().isNotEmpty) {
                Navigator.pop(context);
                _pushPacket(context, ref, value);
              }
            },
          ),
          const SizedBox(height: 12),
          FilledButton.icon(
            icon: const Icon(Icons.send),
            label: const Text('Push'),
            onPressed: () {
              final value = controller.text;
              if (value.trim().isNotEmpty) {
                Navigator.pop(context);
                _pushPacket(context, ref, value);
              }
            },
          ),
        ],
      ),
    ),
  );
}

Future<void> _pushPacket(
    BuildContext context, WidgetRef ref, String text) async {
  try {
    final trimmed = text.trim();
    final isUrl = Uri.tryParse(trimmed)?.hasScheme ?? false;
    final authed = await ref.read(packetsProvider.notifier).push(
          type: isUrl ? 'link' : 'text',
          payload: trimmed,
        );
    if (!authed && context.mounted) {
      showLoginSheet(context);
    }
  } catch (e) {
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Push failed: $e')),
      );
    }
  }
}
