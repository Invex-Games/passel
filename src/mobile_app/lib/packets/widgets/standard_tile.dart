import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../data/packet.dart';
import 'tile_header.dart';

/// Tile for text and link packets.
class StandardTile extends StatelessWidget {
  const StandardTile({super.key, required this.packet, required this.index});

  final Packet packet;
  final int index;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TileHeader(packet: packet, index: index),
        const SizedBox(height: 16),
        Padding(
          padding: const EdgeInsets.only(left: 18.0),
          child: packet.type == PacketType.link
              ? GestureDetector(
                  onTap: () => launchUrl(
                    Uri.parse(packet.payload),
                    mode: LaunchMode.externalApplication,
                  ),
                  child: Text(
                    packet.payload,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.primary,
                      decoration: TextDecoration.underline,
                    ),
                  ),
                )
              : Text(packet.payload),
        ),
        const SizedBox(height: 16),
      ],
    );
  }
}
