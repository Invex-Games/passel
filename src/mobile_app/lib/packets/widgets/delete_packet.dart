import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../api/errors.dart';
import '../../auth/login_page.dart';
import '../data/packet.dart';
import '../packets_provider.dart';
import 'packet_helpers.dart';

/// How long the "Undo" snackbar is shown before the deletion is sent.
const undoDeleteDuration = Duration(seconds: 4);

/// Removes [packet] from the feed and shows an "Undo" snackbar; the packet
/// is deleted on the server once the snackbar closes without being undone.
///
/// [context] must be the *page* context, so feedback (failure snackbar,
/// login prompt) can still be shown after the tile is gone.
void deletePacketWithUndo(BuildContext context, WidgetRef ref, Packet packet) {
  final messenger = ScaffoldMessenger.of(context);
  final notifier = ref.read(packetsProvider.notifier);
  if (!notifier.hide(packet.id)) return;

  // Snackbars queue, which would hold back this deletion behind an earlier
  // one's undo window; closing that one commits it straight away instead.
  messenger.hideCurrentSnackBar();

  final controller = messenger.showSnackBar(
    SnackBar(
      content: Text('${packetNoun(packet, capitalised: true)} deleted'),
      action: SnackBarAction(label: 'Undo', onPressed: () {}),
      duration: undoDeleteDuration,
      // A snackbar with an action otherwise stays until it's dismissed,
      // which would hold back the deletion indefinitely.
      persist: false,
    ),
  );

  controller.closed.then((reason) async {
    if (reason == SnackBarClosedReason.action) {
      notifier.unhide(packet.id);
      return;
    }

    try {
      final authed = await notifier.delete(packet.id);
      if (!authed && context.mounted) await showLoginSheet(context);
    } on SessionExpiredException {
      if (context.mounted) await showLoginSheet(context);
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            'Couldn\'t delete the ${packetNoun(packet)}: ${describeError(e)}',
          ),
        ),
      );
    }
  });
}
