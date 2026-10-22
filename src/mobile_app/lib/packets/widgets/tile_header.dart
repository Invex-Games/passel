import 'package:flutter/material.dart';

import '../data/packet.dart';
import 'packet_helpers.dart';

/// Shared header row used by both [StandardTile] and [FileTile].
///
/// Shows the packet type icon, title, (for text and link packets)
/// share/copy action buttons, and a delete button when [onDelete] is set.
class TileHeader extends StatelessWidget {
  const TileHeader({
    super.key,
    required this.packet,
    required this.index,
    this.onDelete,
  });

  final Packet packet;
  final int index;

  /// Deletes the packet; `null` hides the button (e.g. while signed out).
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final isFile = packet.type == PacketType.file;
    return Container(
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.primaryContainer,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
      ),
      child: Padding(
        padding: const EdgeInsets.all(8.0),
        child: Row(
          mainAxisSize: MainAxisSize.max,
          children: [
            const SizedBox(width: 8),
            Icon(
              switch (packet.type) {
                PacketType.link => Icons.link,
                PacketType.text => Icons.text_snippet,
                PacketType.file =>
                  packet.isImage ? Icons.image : Icons.insert_drive_file,
              },
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                packet.title ?? packetNoun(packet, capitalised: true),
                style: Theme.of(context).textTheme.titleMedium,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            if (isFile && onDelete == null)
              // Keep the header height consistent with action-button rows.
              const SizedBox(height: 48),
            if (!isFile) ...[
              IconButton(
                icon: const Icon(Icons.share),
                tooltip: 'Share',
                onPressed: () => sharePacket(context, packet),
              ),
              IconButton(
                icon: const Icon(Icons.copy),
                tooltip: 'Copy',
                onPressed: () => copyPacket(context, packet),
              ),
            ],
            if (onDelete != null)
              IconButton(
                icon: const Icon(Icons.delete_outline),
                tooltip: 'Delete',
                onPressed: onDelete,
              ),
          ],
        ),
      ),
    );
  }
}
