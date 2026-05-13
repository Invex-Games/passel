import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_repository.dart';

/// In-memory cache that avoids SharedPreferences / Flutter bindings.
class InMemoryPacketCache implements PacketCacheStrategy {
  PacketsState? _state;

  @override
  Future<PacketsState?> load() async => _state;

  @override
  Future<void> save(PacketsState state) async => _state = state;

  @override
  Future<void> clear() async => _state = null;
}

void main() {
  const fakeToken = 'test-token';

  /// Helper: builds a [PacketRepository] backed by the given [MockClient].
  PacketRepository buildRepo(MockClient client) {
    return PacketRepository(
      client: client,
      tokenProvider: () async => fakeToken,
      cache: InMemoryPacketCache(),
      baseUrl: 'https://test.example.com',
    );
  }

  /// Canonical JSON body for a packets response.
  String packetsJson({
    required List<Map<String, dynamic>> packets,
    String? nextCursor,
  }) {
    return jsonEncode({
      'packets': packets,
      'nextCursor': nextCursor,
    });
  }

  // ── fetchPackets ───────────────────────────────────────────────────────────

  group('PacketRepository.fetchPackets', () {
    test('parses a single-page response', () async {
      final client = MockClient((request) async {
        expect(request.headers['Authorization'], 'Bearer $fakeToken');
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '1', 'body': 'hello'},
            {r'$type': 'Link', 'id': '2', 'url': 'https://example.com'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      final result = await repo.fetchPackets();

      expect(result.packets, hasLength(2));
      expect(result.packets[0].id, '1');
      expect(result.packets[0].type, PacketType.text);
      expect(result.packets[1].type, PacketType.link);
      expect(result.hasMore, isFalse);
      expect(result.lastSyncedAt, isNotNull);
    });

    test('passes cursor as query parameter', () async {
      final client = MockClient((request) async {
        expect(request.url.queryParameters['cursor'], 'abc');
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '3', 'body': 'page2'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      final result = await repo.fetchPackets(cursor: 'abc');

      expect(result.packets, hasLength(1));
      expect(result.packets[0].id, '3');
    });

    test('returns nextCursor when present', () async {
      final client = MockClient((request) async {
        return http.Response(
          packetsJson(
            packets: [
              {r'$type': 'Text', 'id': '1', 'body': 'hi'},
            ],
            nextCursor: 'cursor-2',
          ),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      final result = await repo.fetchPackets();

      expect(result.hasMore, isTrue);
      expect(result.nextCursor, 'cursor-2');
    });

    test('throws on non-2xx response', () async {
      final client = MockClient((_) async => http.Response('error', 500));
      final repo = buildRepo(client);

      expect(
        () => repo.fetchPackets(),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTTP 500'),
        )),
      );
    });

    test('throws on redirect (3xx)', () async {
      final client = MockClient((_) async => http.Response('', 302));
      final repo = buildRepo(client);

      expect(
        () => repo.fetchPackets(),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('redirected'),
        )),
      );
    });

    test('throws on HTML response with 200 status', () async {
      final client = MockClient((_) async => http.Response(
            '<html></html>',
            200,
            headers: {'content-type': 'text/html'},
          ));
      final repo = buildRepo(client);

      expect(
        () => repo.fetchPackets(),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTML'),
        )),
      );
    });

    test('includes api-version query parameter', () async {
      final client = MockClient((request) async {
        expect(request.url.queryParameters['api-version'], isNotNull);
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '1', 'body': 'hi'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      await repo.fetchPackets();
    });
  });

  // ── pushPacket ─────────────────────────────────────────────────────────────

  group('PacketRepository.pushPacket', () {
    test('sends correct JSON body and refreshes', () async {
      var callCount = 0;
      final client = MockClient((request) async {
        callCount++;
        if (request.method == 'POST') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          expect(body.keys.first, r'$type');
          expect(body[r'$type'], 'Text');
          expect(body['body'], 'hello');
          return http.Response('{}', 200,
              headers: {'content-type': 'application/json'});
        }
        // GET (refresh after push)
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': 'new', 'body': 'hello'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      final result = await repo.pushPacket(type: 'text', payload: 'hello');

      expect(callCount, 2); // POST + GET
      expect(result.packets.first.id, 'new');
    });

    test('includes title when provided', () async {
      final client = MockClient((request) async {
        if (request.method == 'POST') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          expect(body['title'], 'My Title');
          return http.Response('{}', 200,
              headers: {'content-type': 'application/json'});
        }
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '1', 'body': 'x'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      await repo.pushPacket(type: 'text', payload: 'x', title: 'My Title');
    });

    test('sends link packets with url and name', () async {
      final client = MockClient((request) async {
        if (request.method == 'POST') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          expect(body.keys.first, r'$type');
          expect(body[r'$type'], 'Link');
          expect(body['url'], 'https://example.com');
          expect(body['name'], 'Example');
          return http.Response('{}', 200,
              headers: {'content-type': 'application/json'});
        }
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Link', 'id': '1', 'url': 'https://example.com'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      await repo.pushPacket(
          type: 'link', payload: 'https://example.com', title: 'Example');
    });

    test('sends null title when not provided', () async {
      final client = MockClient((request) async {
        if (request.method == 'POST') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          // The server requires the key to be present, even when null.
          expect(body.containsKey('title'), isTrue);
          expect(body['title'], isNull);
          return http.Response('{}', 200,
              headers: {'content-type': 'application/json'});
        }
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '1', 'body': 'x'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });

      final repo = buildRepo(client);
      await repo.pushPacket(type: 'text', payload: 'x');
    });

    test('throws when push request fails', () async {
      final client = MockClient((_) async => http.Response('fail', 403));
      final repo = buildRepo(client);

      expect(
        () => repo.pushPacket(type: 'text', payload: 'x'),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTTP 403'),
        )),
      );
    });
  });

  // ── validateResponse (static) ──────────────────────────────────────────────

  group('PacketRepository.validateResponse', () {
    test('does not throw for 200 with JSON content-type', () {
      final response = http.Response('{}', 200,
          headers: {'content-type': 'application/json'});
      // Should not throw
      PacketRepository.validateResponse(response);
    });

    test('throws for 200 with text/html content-type', () {
      final response = http.Response('<html></html>', 200,
          headers: {'content-type': 'text/html'});
      expect(
        () => PacketRepository.validateResponse(response),
        throwsA(isA<Exception>()),
      );
    });

    test('throws for 4xx status', () {
      final response = http.Response('not found', 404);
      expect(
        () => PacketRepository.validateResponse(response),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTTP 404'),
        )),
      );
    });

    test('throws for redirect with session-expired hint', () {
      final response = http.Response('', 301);
      expect(
        () => PacketRepository.validateResponse(response),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('session may have expired'),
        )),
      );
    });

    test('includes context label in error message', () {
      final response = http.Response('err', 500);
      expect(
        () => PacketRepository.validateResponse(response, context: 'MyOp'),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('MyOp'),
        )),
      );
    });
  });
}
