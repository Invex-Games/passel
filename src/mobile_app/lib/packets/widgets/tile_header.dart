import 'package:flutter/material.dart';

import '../data/packet.dart';
import 'packet_helpers.dart';

/// Shared header row used by both [StandardTile] and [FileTile].
///
/// Shows the packet type icon, title, and share/copy action buttons.
class TileHeader extends StatelessWidget {
  const TileHeader({
    super.key,
    required this.packet,
    required this.index,
    this.parcelUrl,
  });

  final Packet packet;
  final int index;

  /// For file packets: the cached parcel URL used to retrieve bytes for
  /// share/copy helpers.
  final String? parcelUrl;

  @override
  Widget build(BuildContext context) {
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
                PacketType.file => Icons.image,
              },
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                packet.title ?? 'Packet',
                style: Theme.of(context).textTheme.titleMedium,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            IconButton(
              icon: const Icon(Icons.share),
              tooltip: 'Share',
              onPressed: () =>
                  sharePacket(context, packet, index, parcelUrl: parcelUrl),
            ),
            IconButton(
              icon: const Icon(Icons.copy),
              tooltip: 'Copy',
              onPressed: () =>
                  copyPacket(context, packet, index, parcelUrl: parcelUrl),
            ),
          ],
        ),
      ),
    );
  }
}

