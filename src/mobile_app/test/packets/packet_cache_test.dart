import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_cache.dart';

void main() {
  const cacheKey = 'packets_cache_v3';

  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => SharedPreferences.setMockInitialValues({}));

  group('PacketCache', () {
    test('returns null when nothing is cached', () async {
      expect(await PacketCache.load(), isNull);
    });

    test('round-trips packets, cursor and lastSyncedAt', () async {
      final syncedAt = DateTime.utc(2026, 10, 21, 12, 30);
      final state = PacketsState(
        packets: const [
          Packet(id: 't', type: PacketType.text, payload: 'hi', title: 'T'),
          Packet(id: 'l', type: PacketType.link, payload: 'https://x.com'),
          Packet(
            id: 'f',
            type: PacketType.file,
            payload: 'parcel-1',
            title: 'cat.png',
            contentType: 'image/png',
            isImage: true,
          ),
        ],
        nextCursor: 'cursor-2',
        lastSyncedAt: syncedAt,
        // Transient UI state must not be persisted.
        isLoadingMore: true,
        loadMoreError: 'boom',
      );

      await PacketCache.save(state);
      final loaded = await PacketCache.load();

      expect(loaded, isNotNull);
      expect(loaded!.nextCursor, 'cursor-2');
      expect(loaded.lastSyncedAt, syncedAt);
      expect(loaded.isLoadingMore, isFalse);
      expect(loaded.loadMoreError, isNull);
      expect(loaded.packets.map((p) => p.id), ['t', 'l', 'f']);
      expect(loaded.packets[0].title, 'T');
      expect(loaded.packets[1].type, PacketType.link);
      expect(loaded.packets[2].contentType, 'image/png');
      expect(loaded.packets[2].isImage, isTrue);
    });

    test('clear removes the cached data', () async {
      await PacketCache.save(PacketsState(
        packets: const [Packet(id: '1', type: PacketType.text, payload: 'x')],
      ));
      await PacketCache.clear();
      expect(await PacketCache.load(), isNull);
    });

    test('discards invalid JSON and clears the stored value', () async {
      SharedPreferences.setMockInitialValues({cacheKey: 'not json {'});

      expect(await PacketCache.load(), isNull);

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.containsKey(cacheKey), isFalse);
    });

    test('discards structurally wrong data', () async {
      SharedPreferences.setMockInitialValues({
        cacheKey: jsonEncode({'packets': 'not a list', 'nextCursor': 5}),
      });

      expect(await PacketCache.load(), isNull);

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.containsKey(cacheKey), isFalse);
    });

    test('skips individual packets that fail to parse', () async {
      SharedPreferences.setMockInitialValues({
        cacheKey: jsonEncode({
          'nextCursor': null,
          'packets': [
            {r'$type': 'Text', 'id': '1', 'body': 'ok'},
            {r'$type': 'Hologram', 'id': '2'},
            {r'$type': 'Text', 'id': '3', 'body': ''},
          ],
        }),
      });

      final loaded = await PacketCache.load();

      expect(loaded?.packets.map((p) => p.id), ['1']);
    });
  });
}
