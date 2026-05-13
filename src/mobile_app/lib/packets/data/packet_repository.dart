import 'dart:convert';

import 'package:http/http.dart' as http;

import '../../config/app_config.dart';
import 'packet.dart';
import 'packet_cache.dart';

/// Abstraction over packet cache storage so the repository can be unit-tested
/// without SharedPreferences / Flutter bindings.
abstract class PacketCacheStrategy {
  Future<PacketsState?> load();
  Future<void> save(PacketsState state);
  Future<void> clear();
}

/// Default implementation backed by [PacketCache] (SharedPreferences).
class SharedPrefsPacketCache implements PacketCacheStrategy {
  const SharedPrefsPacketCache();

  @override
  Future<PacketsState?> load() => PacketCache.load();

  @override
  Future<void> save(PacketsState state) => PacketCache.save(state);

  @override
  Future<void> clear() => PacketCache.clear();
}

/// Pure-Dart repository that owns all network + cache I/O for packets.
///
/// Accepts its dependencies via constructor so it can be unit-tested with a
/// fake [http.Client], token supplier, and cache — no Riverpod needed.
///
/// Pass [baseUrl] to override [AppConfig.apiBaseUrl] (useful in tests so that
/// dotenv does not need to be initialised).
class PacketRepository {
  PacketRepository({
    required http.Client client,
    required Future<String> Function() tokenProvider,
    PacketCacheStrategy cache = const SharedPrefsPacketCache(),
    String? baseUrl,
  })  : _client = client,
        _tokenProvider = tokenProvider,
        _cache = cache,
        _baseUrl = baseUrl;

  final http.Client _client;
  final Future<String> Function() _tokenProvider;
  final PacketCacheStrategy _cache;
  /// Optional base URL override; falls back to [AppConfig.apiBaseUrl].
  final String? _baseUrl;

  String get _packetsEndpoint =>
      '${_baseUrl ?? AppConfig.apiBaseUrl}/api/packets';

  // ── Read ───────────────────────────────────────────────────────────────────

  /// Returns the cached [PacketsState] from disk, or `null` if empty/corrupt.
  Future<PacketsState?> loadCached() => _cache.load();

  /// Fetches a page of packets from the API.
  ///
  /// Pass [cursor] to fetch the next page; omit it for the first page.
  /// The result is automatically persisted to the disk cache.
  Future<PacketsState> fetchPackets({String? cursor}) async {
    final token = await _tokenProvider();

    final uri = AppConfig.apiUri(
      _packetsEndpoint,
      queryParameters: cursor != null ? {'cursor': cursor} : null,
    );

    final response = await _client.get(
      uri,
      headers: {'Authorization': 'Bearer $token'},
    );

    validateResponse(response, context: 'Fetch packets');

    final decoded = jsonDecode(response.body) as Map<String, dynamic>;
    final rawList = decoded['packets'] as List<dynamic>;
    final nextCursor = decoded['nextCursor'] as String?;

    final packets = rawList
        .whereType<Map<String, dynamic>>()
        .map(Packet.fromJson)
        .toList(growable: false);

    final result = PacketsState(
      packets: packets,
      nextCursor: nextCursor,
      lastSyncedAt: DateTime.now(),
    );
    await _cache.save(result);
    return result;
  }

  // ── Write ──────────────────────────────────────────────────────────────────

  /// Pushes a new packet and returns the refreshed first page.
  Future<PacketsState> pushPacket({
    required String type,
    required String payload,
    String? title,
  }) async {
    final token = await _tokenProvider();

    final uri = AppConfig.apiUri(_packetsEndpoint);
    final response = await _client.post(
      uri,
      headers: {
        'Authorization': 'Bearer $token',
        'Content-Type': 'application/json',
      },
      body: jsonEncode(Packet.createRequestJson(
        type: PacketType.values.byName(type),
        payload: payload,
        title: title,
      )),
    );

    validateResponse(response, context: 'Push packet');

    // Return the refreshed first page so the caller can update state.
    return fetchPackets();
  }

  // ── Cache management ───────────────────────────────────────────────────────

  /// Persists [state] to disk.
  Future<void> saveCache(PacketsState state) => _cache.save(state);

  /// Clears the on-disk cache (e.g. on sign-out).
  Future<void> clearCache() => _cache.clear();

  // ── Helpers ────────────────────────────────────────────────────────────────

  /// Throws a descriptive [Exception] if [response] indicates a failure.
  ///
  /// Checks:
  /// 1. Non-2xx status → throws immediately.
  /// 2. If the response body looks like HTML (e.g. a sign-in page served after
  ///    a redirect that slipped through), throws an auth-related message.
  static void validateResponse(
    http.Response response, {
    String context = 'Request',
  }) {
    if (response.statusCode >= 200 && response.statusCode < 300) {
      final contentType = response.headers['content-type'] ?? '';
      if (contentType.contains('text/html')) {
        throw Exception(
          '$context received an HTML response instead of JSON. '
          'The session may have expired.',
        );
      }
      return;
    }

    if (response.statusCode >= 300 && response.statusCode < 400) {
      throw Exception(
        '$context was redirected (HTTP ${response.statusCode}). '
        'The session may have expired.',
      );
    }

    throw Exception('$context failed with HTTP ${response.statusCode}');
  }
}
