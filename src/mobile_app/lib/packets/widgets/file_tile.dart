import 'package:flutter/material.dart';

import '../data/packet.dart';
import 'tile_header.dart';

/// Placeholder tile for file packets.
///
/// File packets aren't supported by the server yet, so nothing is fetched —
/// the tile just shows the file name and a hint, and has no actions.
class FileTile extends StatelessWidget {
  const FileTile({super.key, required this.packet, required this.index});

  final Packet packet;
  final int index;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TileHeader(packet: packet, index: index),
        Padding(
          padding: const EdgeInsets.fromLTRB(18, 16, 18, 16),
          child: Text(
            'File packets aren\'t supported yet.',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: cs.onSurface.withValues(alpha: 0.6),
                  fontStyle: FontStyle.italic,
                ),
          ),
        ),
      ],
    );
  }
}
