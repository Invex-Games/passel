import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../data/link_utils.dart';
import '../data/packet.dart';
import 'clamped_text.dart';
import 'tile_header.dart';

/// Tile for text and link packets.
///
/// A note is clamped to [noteMaxLines] lines with a "Show more" toggle
/// (bodies may be up to 64 KB); a link is its URL on one line, ellipsised.
class StandardTile extends StatelessWidget {
  const StandardTile({
    super.key,
    required this.packet,
    required this.index,
    this.onDelete,
  });

  /// How many lines of a note are shown before "Show more".
  static const int noteMaxLines = 8;

  final Packet packet;
  final int index;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    // Only http(s) links are tappable; anything else is shown as plain text.
    final linkUri =
        packet.type == PacketType.link ? tryParseHttpUrl(packet.payload) : null;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TileHeader(packet: packet, index: index, onDelete: onDelete),
        const SizedBox(height: 16),
        Padding(
          padding: const EdgeInsets.only(left: 18.0, right: 18.0),
          child: linkUri != null
              ? GestureDetector(
                  onTap: () => _openLink(context, linkUri),
                  child: Text(
                    packet.payload,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.primary,
                      decoration: TextDecoration.underline,
                    ),
                  ),
                )
              : ClampedText(packet.payload, maxLines: noteMaxLines),
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
