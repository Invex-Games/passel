import 'dart:convert';

import 'package:shared_preferences/shared_preferences.dart';

import 'packet.dart';

// ─── Packet cache ─────────────────────────────────────────────────────────────

/// Persists the current [PacketsState] to [SharedPreferences] so the feed can
/// be shown immediately on the next cold start without a network round-trip.
///
/// Storage layout (single JSON string under [_key]):
/// ```json
/// { "nextCursor": "abc123", "packets": [ { "id": "…", … }, … ] }
/// ```
class PacketCache {
  PacketCache._();

  static const String _key = 'packets_cache_v3';

  /// Returns the last saved [PacketsState], or `null` if nothing is cached.
  static Future<PacketsState?> load() async {
    final prefs = await SharedPreferences.getInstance();
    final raw = prefs.getString(_key);
    if (raw == null) return null;

    try {
      final map = jsonDecode(raw) as Map<String, dynamic>;
      final rawList = map['packets'] as List<dynamic>;
      final packets = rawList
          .whereType<Map<String, dynamic>>()
          .map(Packet.fromJson)
          .toList(growable: false);
      final nextCursor = map['nextCursor'] as String?;
      final lastSyncedRaw = map['lastSyncedAt'] as String?;
      final lastSyncedAt =
          lastSyncedRaw != null ? DateTime.tryParse(lastSyncedRaw) : null;
      return PacketsState(
        packets: packets,
        nextCursor: nextCursor,
        lastSyncedAt: lastSyncedAt,
      );
    } catch (_) {
      // Corrupt cache — discard silently.
      await clear();
      return null;
    }
  }

  /// Persists [state] to disk, replacing any previously stored data.
  static Future<void> save(PacketsState state) async {
    final prefs = await SharedPreferences.getInstance();
    final encoded = jsonEncode({
      'nextCursor': state.nextCursor,
      'lastSyncedAt': state.lastSyncedAt?.toIso8601String(),
      'packets': state.packets.map((p) => p.toJson()).toList(),
    });
    await prefs.setString(_key, encoded);
  }

  /// Removes the cached packets (call on sign-out to avoid stale data for
  /// a different user opening the app on the same device).
  static Future<void> clear() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.remove(_key);
  }
}
