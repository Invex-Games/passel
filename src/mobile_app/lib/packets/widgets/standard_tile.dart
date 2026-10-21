import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../data/link_utils.dart';
import '../data/packet.dart';
import 'tile_header.dart';

/// Tile for text and link packets.
class StandardTile extends StatelessWidget {
  const StandardTile({super.key, required this.packet, required this.index});

  final Packet packet;
  final int index;

  @override
  Widget build(BuildContext context) {
    // Only http(s) links are tappable; anything else is shown as plain text.
    final linkUri =
        packet.type == PacketType.link ? tryParseHttpUrl(packet.payload) : null;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TileHeader(packet: packet, index: index),
        const SizedBox(height: 16),
        Padding(
          padding: const EdgeInsets.only(left: 18.0),
          child: linkUri != null
              ? GestureDetector(
                  onTap: () => _openLink(context, linkUri),
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

  /// Launches [uri] externally; never throws — failures show a snackbar.
  Future<void> _openLink(BuildContext context, Uri uri) async {
    final messenger = ScaffoldMessenger.maybeOf(context);
    var launched = false;
    try {
      launched = await launchUrl(uri, mode: LaunchMode.externalApplication);
    } catch (_) {
      launched = false;
    }
    if (!launched) {
      messenger?.showSnackBar(
        const SnackBar(content: Text('Couldn\'t open this link')),
      );
    }
  }
}
