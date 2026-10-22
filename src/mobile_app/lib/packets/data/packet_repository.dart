import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import '../../auth/auth_helpers.dart';
import '../../auth/data/authorization.dart';
import '../../config/app_config.dart';
import 'packet.dart';
import 'packet_cache.dart';
import 'server_sent_events.dart';

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
    this.eventStreamIdleTimeout = const Duration(seconds: 45),
  })  : _client = client,
        _tokenProvider = tokenProvider,
        _cache = cache,
        _baseUrl = baseUrl;

  final http.Client _client;
  final TokenProvider _tokenProvider;
  final PacketCacheStrategy _cache;

  /// How long the event stream may stay silent before the connection is
  /// treated as dead.  The server sends a heartbeat every 20 seconds.
  final Duration eventStreamIdleTimeout;

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
      (token) => _client.get(uri, headers: {'Authorization': authorizationHeader(token)}),
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

  // ── Live events ────────────────────────────────────────────────────────────


  /// Opens the live packet event stream.
  ///
  /// Emits a [PacketsResyncEvent] once connected, then a [PacketCreatedEvent]
  /// per new packet.  The stream ends if the server closes the connection
  /// and errors if it fails ([SessionExpiredException] if the session is
  /// rejected); either way the caller should reconnect.  Cancelling the
  /// subscription closes the connection.
  Stream<PacketEvent> watchEvents() {
    // Aborts the request if cancelled while connecting.
    final abort = Completer<void>();
    StreamSubscription<ServerSentEvent>? events;
    late final StreamController<PacketEvent> controller;

    Future<void> connect() async {
      try {
        final uri = AppConfig.apiUri('$_packetsEndpoint/events');
        final response = await sendWithAuthRetry(
          _tokenProvider,
          (token) => _client.send(
            http.AbortableRequest('GET', uri, abortTrigger: abort.future)
              ..headers.addAll({
                'Authorization': authorizationHeader(token),
                'Accept': 'text/event-stream',
              }),
          ),
        );

        // Cancelled while connecting, by a client that can't abort.
        if (abort.isCompleted) {
          await response.stream.listen(null).cancel();
          return;
        }

        await _validateEventStream(response);

        events = decodeServerSentEvents(
          response.stream.timeout(eventStreamIdleTimeout),
        ).listen(
          (event) {
            final packetEvent = _toPacketEvent(event);
            if (packetEvent != null) controller.add(packetEvent);
          },
          onError: (Object e, StackTrace s) {
            controller.addError(e, s);
            controller.close();
          },
          onDone: controller.close,
          cancelOnError: true,
        );
      } catch (e, s) {
        // Errors caused by the caller cancelling aren't worth reporting.
        if (!abort.isCompleted) controller.addError(e, s);
        await controller.close();
      }
    }

    controller = StreamController<PacketEvent>(
      onListen: () {
        unawaited(connect());
      },
      onCancel: () {
        if (!abort.isCompleted) abort.complete();
        return events?.cancel();
      },
    );
    return controller.stream;
  }

  /// Throws like [validateResponse] unless [response] is an event stream.
  static Future<void> _validateEventStream(
    http.StreamedResponse response,
  ) async {
    final contentType = response.headers['content-type'] ?? '';
    final isSuccess = response.statusCode >= 200 && response.statusCode < 300;
    if (isSuccess && contentType.startsWith('text/event-stream')) return;

    const context = 'Watch packets';
    validateResponse(await http.Response.fromStream(response),
        context: context);
    throw Exception('$context received "$contentType" instead of an event '
        'stream.');
  }

  /// Maps a server-sent event to a [PacketEvent], or `null` for heartbeats
  /// and events this version of the app doesn't understand.
  static PacketEvent? _toPacketEvent(ServerSentEvent event) {
    switch (event.event) {
      case 'resync':
        return const PacketsResyncEvent();
      case 'packet-created':
        try {
          final decoded = jsonDecode(event.data);
          if (decoded is Map<String, dynamic>) {
            return PacketCreatedEvent(Packet.fromJson(decoded));
          }
        } on FormatException {
          // Unparseable or unsupported packet — skip it like the feed does.
        }
        return null;
      case 'packet-deleted':
        try {
          final decoded = jsonDecode(event.data);
          if (decoded is Map<String, dynamic> && decoded['id'] is String) {
            return PacketDeletedEvent(decoded['id'] as String);
          }
        } on FormatException {
          // Unparseable — skip it.
        }
        return null;
      default:
        return null;
    }
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
          'Authorization': authorizationHeader(token),
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

  /// Deletes the packet with [id].
  ///
  /// A packet that no longer exists (e.g. deleted on another device) counts
  /// as deleted.  Throws if the request fails.
  Future<void> deletePacket(String id) async {
    final uri =
        AppConfig.apiUri('$_packetsEndpoint/${Uri.encodeComponent(id)}');

    final response = await sendWithAuthRetry(
      _tokenProvider,
      (token) =>
          _client.delete(uri, headers: {'Authorization': authorizationHeader(token)}),
    );

    if (response.statusCode == 404) return;
    validateResponse(response, context: 'Delete packet');
  }

  // ── Cache management ───────────────────────────────────────────────────────

  /// Persists [state] to disk.
  Future<void> saveCache(PacketsState state) => _cache.save(state);

  /// Clears the on-disk cache (e.g. on sign-out).
  Future<void> clearCache() => _cache.clear();
}
