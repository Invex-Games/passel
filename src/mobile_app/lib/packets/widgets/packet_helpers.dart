import 'package:cached_network_image_ce/cached_network_image.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:share_plus/share_plus.dart';

import '../data/packet.dart';

/// Copies the most relevant text from a [Packet] to the clipboard.
///
/// When the packet has a title **and** a payload worth copying (i.e. not a file
/// packet whose payload is just a parcel ID), a bottom sheet is shown letting
/// the user choose between the title, the content, or both.  Otherwise the
/// single meaningful value is copied immediately.
void copyPacket(BuildContext context, Packet packet, int index,
    {String? parcelUrl}) {
  // For file packets, offer image copy options.
  if (packet.type == PacketType.file) {
    _showImageCopySheet(context, packet, index, parcelUrl);
    return;
  }

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

/// Shares the most relevant text from a [Packet] via the native share sheet.
///
/// When the packet has a title and shareable content, a bottom sheet lets the
/// user choose between the title, the content, or both. Otherwise the single
/// meaningful value is shared immediately.
void sharePacket(BuildContext context, Packet packet, int index,
    {String? parcelUrl}) {
  // For file packets, share the image file.
  if (packet.type == PacketType.file) {
    _shareImage(context, packet, index, parcelUrl);
    return;
  }

  // No title → share payload directly.
  if (packet.title == null || packet.title!.isEmpty) {
    Share.share(packet.payload);
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
                Share.share(packet.title!);
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
                Share.share(packet.payload);
              },
            ),
            ListTile(
              leading: const Icon(Icons.share),
              title: const Text('Share both'),
              onTap: () {
                Navigator.pop(ctx);
                Share.share('${packet.title!}\n${packet.payload}');
              },
            ),
          ],
        ),
      );
    },
  );
}

// ─── Image helpers ────────────────────────────────────────────────────────────

/// Retrieves the cached file for [parcelUrl] from the [DefaultCacheManager].
/// Returns the file path, or `null` if the image is not available.
Future<String?> _getCachedFilePath(String parcelUrl) async {
  final manager = DefaultCacheManager();
  try {
    // First check disk cache.
    final cached = await manager.getFileFromCache(parcelUrl);
    if (cached != null) {
      return cached.file.path;
    }
    // Not cached yet — stream-download and return the first FileInfo.
    await for (final response in manager.getFileStream(parcelUrl)) {
      if (response is FileInfo) {
        return response.file.path;
      }
    }
    return null;
  } catch (_) {
    return null;
  }
}

/// Shows a bottom sheet for copying image-related content.
void _showImageCopySheet(
    BuildContext context, Packet packet, int index, String? parcelUrl) {
  final title = packet.title ?? 'File ${index + 1}';

  // No parcel URL — just copy the title.
  if (parcelUrl == null) {
    _copyAndConfirm(context, title);
    return;
  }

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
              subtitle:
                  Text(title, maxLines: 1, overflow: TextOverflow.ellipsis),
              onTap: () {
                Navigator.pop(ctx);
                _copyAndConfirm(context, title);
              },
            ),
            ListTile(
              leading: const Icon(Icons.image),
              title: const Text('Share image'),
              subtitle: const Text('Send image via share sheet'),
              onTap: () async {
                Navigator.pop(ctx);
                final path = await _getCachedFilePath(parcelUrl);
                if (path != null) {
                  await Share.shareXFiles(
                    [XFile(path, mimeType: 'image/png')],
                    text: title,
                  );
                }
              },
            ),
          ],
        ),
      );
    },
  );
}

/// Shares the actual image file via the native share sheet.
void _shareImage(
    BuildContext context, Packet packet, int index, String? parcelUrl) {
  final title = packet.title ?? 'File ${index + 1}';

  // No parcel URL — fall back to sharing the title text.
  if (parcelUrl == null) {
    Share.share(title);
    return;
  }

  () async {
    final path = await _getCachedFilePath(parcelUrl);
    if (path != null) {
      await Share.shareXFiles(
        [XFile(path, mimeType: 'image/png')],
        text: title,
      );
    } else {
      // Fallback: share title text if file isn't available.
      Share.share(title);
    }
  }();
}

