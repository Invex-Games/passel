import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:share_plus/share_plus.dart';

import '../data/packet.dart';

/// Copies the most relevant text from a text or link [Packet] to the
/// clipboard.
///
/// When the packet has a title **and** a payload, a bottom sheet is shown
/// letting the user choose between the title, the content, or both.
/// Otherwise the payload is copied immediately.
void copyPacket(BuildContext context, Packet packet) {
  // No title → copy payload directly.
  if (packet.title == null || packet.title!.isEmpty) {
    _copyAndConfirm(context, packet.payload);
    return;
  }

  // Has both title and content — let the user choose.
  showModalBottomSheet<void>(
    context: context,
    builder: (ctx) {
      return SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.title),
              title: const Text('Copy title'),
              subtitle: Text(
                packet.title!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              onTap: () {
                Navigator.pop(ctx);
                _copyAndConfirm(context, packet.title!);
              },
            ),
            ListTile(
              leading: const Icon(Icons.notes),
              title: const Text('Copy content'),
              subtitle: Text(
                packet.payload,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              onTap: () {
                Navigator.pop(ctx);
                _copyAndConfirm(context, packet.payload);
              },
            ),
            ListTile(
              leading: const Icon(Icons.copy_all),
              title: const Text('Copy both'),
              onTap: () {
                Navigator.pop(ctx);
                _copyAndConfirm(
                  context,
                  '${packet.title!}\n${packet.payload}',
                );
              },
            ),
          ],
        ),
      );
    },
  );
}

void _copyAndConfirm(BuildContext context, String text) {
  Clipboard.setData(ClipboardData(text: text));
  ScaffoldMessenger.of(context).showSnackBar(
    const SnackBar(
      content: Text('Copied to clipboard'),
      duration: Duration(seconds: 1),
    ),
  );
}

/// Shares the most relevant text from a text or link [Packet] via the native
/// share sheet.
///
/// When the packet has a title and content, a bottom sheet lets the user
/// choose between the title, the content, or both. Otherwise the payload is
/// shared immediately.
void sharePacket(BuildContext context, Packet packet) {
  // No title → share payload directly.
  if (packet.title == null || packet.title!.isEmpty) {
    SharePlus.instance.share(ShareParams(text: packet.payload));
    return;
  }

  // Has both title and content — let the user choose.
  showModalBottomSheet<void>(
    context: context,
    builder: (ctx) {
      return SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.title),
              title: const Text('Share title'),
              subtitle: Text(
                packet.title!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              onTap: () {
                Navigator.pop(ctx);
                SharePlus.instance.share(ShareParams(text: packet.title!));
              },
            ),
            ListTile(
              leading: const Icon(Icons.notes),
              title: const Text('Share content'),
              subtitle: Text(
                packet.payload,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              onTap: () {
                Navigator.pop(ctx);
                SharePlus.instance.share(ShareParams(text: packet.payload));
              },
            ),
            ListTile(
              leading: const Icon(Icons.share),
              title: const Text('Share both'),
              onTap: () {
                Navigator.pop(ctx);
                SharePlus.instance.share(
                  ShareParams(text: '${packet.title!}\n${packet.payload}'),
                );
              },
            ),
          ],
        ),
      );
    },
  );
}
