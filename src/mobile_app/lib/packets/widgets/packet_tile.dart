import 'package:flutter/material.dart';

import '../data/packet.dart';
import 'file_tile.dart';
import 'standard_tile.dart';

/// Routes to the correct tile widget based on [Packet.type].
class PacketTile extends StatelessWidget {
  const PacketTile({
    super.key,
    required this.packet,
    required this.index,
    this.onDelete,
  });

  final Packet packet;
  final int index;

  /// Deletes the packet; `null` hides the delete button.
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16.0),
      child: Card.filled(
        color: Theme.of(context).colorScheme.primaryContainer.withValues(alpha: 0.4),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        child: packet.type == PacketType.file
            ? FileTile(packet: packet, index: index, onDelete: onDelete)
            : StandardTile(packet: packet, index: index, onDelete: onDelete),
      ),
    );
  }
}
