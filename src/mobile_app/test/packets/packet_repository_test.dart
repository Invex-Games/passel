import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/api/errors.dart';
import 'package:passel_mobileapp/auth/auth_helpers.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_repository.dart';

import '../fakes.dart';

void main() {
  const fakeToken = 'test-token';

  /// Helper: builds a [PacketRepository] backed by the given [MockClient].
  PacketRepository buildRepo(MockClient client, {InMemoryPacketCache? cache}) {
    return PacketRepository(
      client: client,
      tokenProvider: ({bool forceRefresh = false}) async => fakeToken,
      cache: cache ?? InMemoryPacketCache(),
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

  /// A successful POST response echoing the created packet.
  http.Response createdResponse(Map<String, dynamic> body) => http.Response(
        jsonEncode(body),
        201,
        headers: {'content-type': 'application/json'},
      );

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

    test('keeps file packets and skips unknown packet types', () async {
      final client = MockClient((_) async => http.Response(
            packetsJson(packets: [
              {r'$type': 'Text', 'id': '1', 'body': 'hi'},
              {
                r'$type': 'File',
                'id': '2',
                'fileName': 'cat.png',
                'contentType': 'image/png',
                'isImage': true,
              },
              {r'$type': 'Video', 'id': '3', 'url': 'x'},
            ]),
            200,
            headers: {'content-type': 'application/json'},
          ));

      final result = await buildRepo(client).fetchPackets();

      expect(result.packets.map((p) => p.id), ['1', '2']);
      final file = result.packets[1];
      expect(file.type, PacketType.file);
      expect(file.title, 'cat.png');
      expect(file.contentType, 'image/png');
      expect(file.isImage, isTrue);
    });

    test('does not write the cache itself', () async {
      final cache = InMemoryPacketCache();
      final client = MockClient((_) async => http.Response(
            packetsJson(packets: [
              {r'$type': 'Text', 'id': '1', 'body': 'hi'},
            ]),
            200,
            headers: {'content-type': 'application/json'},
          ));

      await buildRepo(client, cache: cache).fetchPackets();

      expect(cache.state, isNull);
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

  // ── Failures ───────────────────────────────────────────────────────────────

  group('PacketRepository failures', () {
    test('carry the problem details the server sent', () async {
      final client = MockClient((_) async => http.Response(
            jsonEncode({
              'title': 'One or more validation errors occurred.',
              'status': 400,
              'errors': {
                'body': ['Body must be non-empty and at most 65536 characters.'],
              },
            }),
            400,
            headers: {'content-type': 'application/problem+json'},
          ));

      await expectLater(
        buildRepo(client).pushPacket(type: PacketType.text, payload: ''),
        throwsA(isA<ApiException>()
            .having((e) => e.status, 'status', 400)
            .having((e) => e.errors.keys, 'fields', ['body'])
            .having((e) => e.context, 'context', 'Push packet')),
      );
    });

    test('surface transport failures as NetworkException', () async {
      final client = MockClient(
          (_) async => throw const SocketException('Network unreachable'));

      await expectLater(
        buildRepo(client).fetchPackets(),
        throwsA(isA<NetworkException>()
            .having((e) => e.cause, 'cause', isA<SocketException>())),
      );
    });

    test('include failures the http client reports', () async {
      final client = MockClient((_) async => throw http.ClientException(
          'Connection closed before full header was received'));

      await expectLater(
        buildRepo(client).deletePacket('a'),
        throwsA(isA<NetworkException>()),
      );
    });
  });

  // ── 401 handling ───────────────────────────────────────────────────────────

  group('PacketRepository 401 retry', () {
    test('refreshes the token and retries once on 401', () async {
      final forceRefreshCalls = <bool>[];
      final seenTokens = <String?>[];
      final client = MockClient((request) async {
        seenTokens.add(request.headers['Authorization']);
        if (seenTokens.length == 1) return http.Response('', 401);
        return http.Response(
          packetsJson(packets: [
            {r'$type': 'Text', 'id': '1', 'body': 'hi'},
          ]),
          200,
          headers: {'content-type': 'application/json'},
        );
      });
      final repo = PacketRepository(
        client: client,
        tokenProvider: ({bool forceRefresh = false}) async {
          forceRefreshCalls.add(forceRefresh);
          return forceRefresh ? 'fresh-token' : 'stale-token';
        },
        cache: InMemoryPacketCache(),
        baseUrl: 'https://test.example.com',
      );

      final result = await repo.fetchPackets();

      expect(result.packets.single.id, '1');
      expect(forceRefreshCalls, [false, true]);
      expect(seenTokens, ['Bearer stale-token', 'Bearer fresh-token']);
    });

    test('throws SessionExpiredException when the retry is also 401',
        () async {
      var calls = 0;
      final client = MockClient((_) async {
        calls++;
        return http.Response('', 401);
      });

      await expectLater(
        buildRepo(client).fetchPackets(),
        throwsA(isA<SessionExpiredException>()),
      );
      expect(calls, 2);
    });

    test('propagates SessionExpiredException from the token provider',
        () async {
      final client = MockClient((_) async => http.Response('', 401));
      final repo = PacketRepository(
        client: client,
        tokenProvider: ({bool forceRefresh = false}) async {
          if (forceRefresh) throw const SessionExpiredException();
          return 'stale-token';
        },
        cache: InMemoryPacketCache(),
        baseUrl: 'https://test.example.com',
      );

      await expectLater(
        repo.pushPacket(type: PacketType.text, payload: 'x'),
        throwsA(isA<SessionExpiredException>()),
      );
    });
  });

  // ── pushPacket ─────────────────────────────────────────────────────────────

  group('PacketRepository.pushPacket', () {
    test('sends correct JSON body and returns the created packet', () async {
      var callCount = 0;
      final client = MockClient((request) async {
        callCount++;
        expect(request.method, 'POST');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body.keys.first, r'$type');
        expect(body[r'$type'], 'Text');
        expect(body['body'], 'hello');
        return createdResponse(
            {r'$type': 'Text', 'id': 'new', 'title': null, 'body': 'hello'});
      });

      final repo = buildRepo(client);
      final created =
          await repo.pushPacket(type: PacketType.text, payload: 'hello');

      expect(callCount, 1); // POST only — no re-fetch of page 1
      expect(created?.id, 'new');
      expect(created?.payload, 'hello');
    });

    test('returns null when the response body is not a packet', () async {
      final client = MockClient((_) async =>
          http.Response('', 204, headers: {'content-type': 'text/plain'}));

      final created = await buildRepo(client)
          .pushPacket(type: PacketType.text, payload: 'x');

      expect(created, isNull);
    });

    test('includes title when provided', () async {
      final client = MockClient((request) async {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['title'], 'My Title');
        return createdResponse(
            {r'$type': 'Text', 'id': '1', 'title': 'My Title', 'body': 'x'});
      });

      final repo = buildRepo(client);
      await repo.pushPacket(
          type: PacketType.text, payload: 'x', title: 'My Title');
    });

    test('sends link packets with url and name', () async {
      final client = MockClient((request) async {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body.keys.first, r'$type');
        expect(body[r'$type'], 'Link');
        expect(body['url'], 'https://example.com');
        expect(body['name'], 'Example');
        return createdResponse({
          r'$type': 'Link',
          'id': '1',
          'name': 'Example',
          'url': 'https://example.com',
        });
      });

      final repo = buildRepo(client);
      final created = await repo.pushPacket(
          type: PacketType.link,
          payload: 'https://example.com',
          title: 'Example');
      expect(created?.type, PacketType.link);
    });

    test('sends null title when not provided', () async {
      final client = MockClient((request) async {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        // The server requires the key to be present, even when null.
        expect(body.containsKey('title'), isTrue);
        expect(body['title'], isNull);
        return createdResponse({r'$type': 'Text', 'id': '1', 'body': 'x'});
      });

      final repo = buildRepo(client);
      await repo.pushPacket(type: PacketType.text, payload: 'x');
    });

    test('throws when push request fails', () async {
      final client = MockClient((_) async => http.Response('fail', 403));
      final repo = buildRepo(client);

      expect(
        () => repo.pushPacket(type: PacketType.text, payload: 'x'),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTTP 403'),
        )),
      );
    });
  });

  // ── validateResponse ───────────────────────────────────────────────────────

  group('validateResponse', () {
    test('does not throw for 200 with JSON content-type', () {
      final response = http.Response('{}', 200,
          headers: {'content-type': 'application/json'});
      // Should not throw
      validateResponse(response);
    });

    test('throws for 200 with text/html content-type', () {
      final response = http.Response('<html></html>', 200,
          headers: {'content-type': 'text/html'});
      expect(
        () => validateResponse(response),
        throwsA(isA<Exception>()),
      );
    });

    test('throws SessionExpiredException for 401', () {
      final response = http.Response('', 401);
      expect(
        () => validateResponse(response),
        throwsA(isA<SessionExpiredException>()),
      );
    });

    test('throws for 4xx status', () {
      final response = http.Response('not found', 404);
      expect(
        () => validateResponse(response),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('HTTP 404'),
        )),
      );
    });

    test('throws an ApiException carrying the problem details', () {
      final response = http.Response(
        jsonEncode({
          'title': 'Bad Request',
          'status': 400,
          'detail': 'The cursor is invalid.',
        }),
        400,
        headers: {'content-type': 'application/problem+json'},
      );
      expect(
        () => validateResponse(response, context: 'Fetch packets'),
        throwsA(isA<ApiException>()
            .having((e) => e.status, 'status', 400)
            .having((e) => e.detail, 'detail', 'The cursor is invalid.')
            .having(
              (e) => e.toString(),
              'toString',
              'Fetch packets failed with HTTP 400: The cursor is invalid.',
            )),
      );
    });

    test('throws for redirect with session-expired hint', () {
      final response = http.Response('', 301);
      expect(
        () => validateResponse(response),
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
        () => validateResponse(response, context: 'MyOp'),
        throwsA(isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('MyOp'),
        )),
      );
    });
  });
}
