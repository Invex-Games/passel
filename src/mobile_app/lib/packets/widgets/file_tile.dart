import 'package:cached_network_image_ce/cached_network_image.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:fullscreen_image_viewer/fullscreen_image_viewer.dart';

import '../data/packet.dart';
import '../data/parcel_cache_manager.dart';
import 'tile_header.dart';

/// Tile for file packets — uses [CachedNetworkImage] for fetching, caching,
/// and displaying the parcel image with tap-to-fullscreen.
class FileTile extends ConsumerWidget {
  const FileTile({super.key, required this.packet, required this.index});

  final Packet packet;
  final int index;

  String get _heroTag => 'packet-file-${packet.id}';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final url = parcelUrl(packet.payload);
    final cacheManager = ref.watch(parcelCacheManagerProvider);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TileHeader(packet: packet, index: index, parcelUrl: url),
        GestureDetector(
          onTap: () => _showFullScreenImage(context, url, cacheManager),
          child: ClipRRect(
            borderRadius: const BorderRadius.only(
              bottomLeft: Radius.circular(16),
              bottomRight: Radius.circular(16),
            ),
            child: Container(
              height: 200,
              width: double.infinity,
              color: Theme.of(context)
                  .colorScheme
                  .surfaceContainerHighest
                  .withValues(alpha: 0.3),
              child: Hero(
                tag: _heroTag,
                child: CachedNetworkImage(
                  imageUrl: url,
                  cacheManager: cacheManager,
                  memCacheWidth: 600,
                  fit: BoxFit.cover,
                  width: double.infinity,
                  height: double.infinity,
                  fadeInDuration: const Duration(milliseconds: 300),
                  fadeOutDuration: const Duration(milliseconds: 300),
                  placeholder: (context, url) => const Center(
                    child: CircularProgressIndicator(),
                  ),
                  unsupportedImageBuilder: (context, url, bytes) {
                    return SvgPicture.memory(
                      bytes,
                      fit: BoxFit.contain,
                      width: double.infinity,
                      height: double.infinity,
                    );
                  },
                  errorBuilder: (context, error, stackTrace) => Container(
                    height: 200,
                    decoration: BoxDecoration(
                      color: Theme.of(context)
                          .colorScheme
                          .errorContainer
                          .withValues(alpha: 0.2),
                      borderRadius: const BorderRadius.only(
                        bottomLeft: Radius.circular(16),
                        bottomRight: Radius.circular(16),
                      ),
                    ),
                    alignment: Alignment.center,
                    child: Padding(
                      padding: const EdgeInsets.all(12),
                      child: Text(
                        'Failed to load file\n$error',
                        textAlign: TextAlign.center,
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Future<void> _showFullScreenImage(
    BuildContext context,
    String url,
    BaseCacheManager cacheManager,
  ) async {
    final provider = CachedNetworkImageProvider(
      url,
      cacheManager: cacheManager,
    );
    // Precache full-res so it's already decoded when the Hero flight starts.
    await precacheImage(provider, context);
    if (!context.mounted) return;

    return FullscreenImageViewer.open(
      context: context,
      child: Hero(
        tag: _heroTag,
        child: CachedNetworkImage(
          imageUrl: url,
          cacheManager: cacheManager,
          fit: BoxFit.contain,
          unsupportedImageBuilder: (context, url, bytes) {
            return SvgPicture.memory(bytes, fit: BoxFit.contain);
          },
        ),
      ),
      closeWidget: const CircleAvatar(
        radius: 18,
        backgroundColor: Colors.black54,
        child: Icon(Icons.close, color: Colors.white),
      ),
    );
  }
}
