import 'dart:convert';

import 'package:http/http.dart' as http;

import '../../auth/auth_helpers.dart';
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

/// Repository that owns all network + cache I/O for packets.
///
/// Accepts its dependencies via constructor so it can be unit-tested with a
/// fake [http.Client], token supplier, and cache — no Riverpod needed.
///
/// Pass [baseUrl] to override [AppConfig.apiBaseUrl] in tests.
class PacketRepository {
  PacketRepository({
    required http.Client client,
    required TokenProvider tokenProvider,
    PacketCacheStrategy cache = const SharedPrefsPacketCache(),
    String? baseUrl,
  })  : _client = client,
        _tokenProvider = tokenProvider,
        _cache = cache,
        _baseUrl = baseUrl;

  final http.Client _client;
  final TokenProvider _tokenProvider;
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
  /// The result is **not** cached — the caller decides whether the page is
  /// still current and persists it via [saveCache].
  Future<PacketsState> fetchPackets({String? cursor}) async {
    final uri = AppConfig.apiUri(
      _packetsEndpoint,
      queryParameters: cursor != null ? {'cursor': cursor} : null,
    );

    final response = await sendWithAuthRetry(
      _tokenProvider,
      (token) => _client.get(uri, headers: {'Authorization': 'Bearer $token'}),
    );

    validateResponse(response, context: 'Fetch packets');

    final decoded = jsonDecode(response.body) as Map<String, dynamic>;
    final rawList = decoded['packets'] as List<dynamic>;
    final nextCursor = decoded['nextCursor'] as String?;

    return PacketsState(
      packets: Packet.listFromJson(rawList),
      nextCursor: nextCursor,
      lastSyncedAt: DateTime.now(),
    );
  }

  // ── Write ──────────────────────────────────────────────────────────────────

  /// Pushes a new text or link packet.
  ///
  /// Returns the created packet as echoed back by the server, or `null` if
  /// the response body couldn't be parsed as a packet (the push itself still
  /// succeeded — the caller should refresh instead).
  Future<Packet?> pushPacket({
    required PacketType type,
    required String payload,
    String? title,
  }) async {
    final uri = AppConfig.apiUri(_packetsEndpoint);
    final body = jsonEncode(Packet.createRequestJson(
      type: type,
      payload: payload,
      title: title,
    ));

    final response = await sendWithAuthRetry(
      _tokenProvider,
      (token) => _client.post(
        uri,
        headers: {
          'Authorization': 'Bearer $token',
          'Content-Type': 'application/json',
        },
        body: body,
      ),
    );

    validateResponse(response, context: 'Push packet');

    try {
      final decoded = jsonDecode(response.body);
      if (decoded is Map<String, dynamic>) return Packet.fromJson(decoded);
    } on FormatException {
      // Fall through — unparseable body.
    }
    return null;
  }

  // ── Cache management ───────────────────────────────────────────────────────

  /// Persists [state] to disk.
  Future<void> saveCache(PacketsState state) => _cache.save(state);

  /// Clears the on-disk cache (e.g. on sign-out).
  Future<void> clearCache() => _cache.clear();
}
