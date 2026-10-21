import 'dart:async';
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/auth/auth_helpers.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_repository.dart';

import '../fakes.dart';

void main() {
  const eventStreamHeaders = {'content-type': 'text/event-stream'};

  /// Builds a [PacketRepository] whose requests are answered by [handler];
  /// [tokens] records the `forceRefresh` flag of each token request.
  PacketRepository buildRepo(
    MockClientStreamHandler handler, {
    List<bool>? tokens,
    Duration idleTimeout = const Duration(seconds: 45),
  }) {
    return PacketRepository(
      client: MockClient.streaming(handler),
      tokenProvider: ({bool forceRefresh = false}) async {
        tokens?.add(forceRefresh);
        return forceRefresh ? 'fresh-token' : 'token';
      },
      cache: InMemoryPacketCache(),
      baseUrl: 'https://test.example.com',
      eventStreamIdleTimeout: idleTimeout,
    );
  }

  http.StreamedResponse eventStream(Stream<String> body) =>
      http.StreamedResponse(body.map(utf8.encode), 200,
          headers: eventStreamHeaders);

  group('PacketRepository.watchEvents', () {
    test('requests the event stream and maps its events', () async {
      late http.BaseRequest sent;
      final repo = buildRepo((request, _) async {
        sent = request;
        return eventStream(Stream.fromIterable([
          'event: resync\ndata: \n\n',
          'event: ping\ndata: \n\n',
          'event: packet-created\n'
              'data: {"\$type":"Link","id":"p1","name":null,'
              '"url":"https://example.com"}\n\n',
          // Skipped: unknown event, unsupported packet, malformed JSON.
          'event: packet-deleted\ndata: {"id":"p1"}\n\n',
          'event: packet-created\ndata: {"\$type":"Video","id":"p2"}\n\n',
          'event: packet-created\ndata: {not json\n\n',
        ]));
      });

      final events = await repo.watchEvents().toList();

      expect(sent.url.toString(),
          'https://test.example.com/api/packets/events?api-version=1');
      expect(sent.headers['Authorization'], 'Bearer token');
      expect(sent.headers['Accept'], 'text/event-stream');
      expect(events, hasLength(2));
      expect(events[0], isA<PacketsResyncEvent>());
      final created = events[1] as PacketCreatedEvent;
      expect(created.packet.id, 'p1');
      expect(created.packet.type, PacketType.link);
      expect(created.packet.payload, 'https://example.com');
    });

    test('retries once with a refreshed token after HTTP 401', () async {
      final tokens = <bool>[];
      final authHeaders = <String?>[];
      final repo = buildRepo((request, _) async {
        authHeaders.add(request.headers['Authorization']);
        if (authHeaders.length == 1) {
          return http.StreamedResponse(Stream.value(utf8.encode('no')), 401);
        }
        return eventStream(Stream.value('event: resync\ndata: \n\n'));
      }, tokens: tokens);

      final events = await repo.watchEvents().toList();

      expect(tokens, [false, true]);
      expect(authHeaders, ['Bearer token', 'Bearer fresh-token']);
      expect(events.single, isA<PacketsResyncEvent>());
    });

    test('errors with SessionExpiredException when still rejected', () async {
      final repo = buildRepo((_, __) async =>
          http.StreamedResponse(const Stream.empty(), 401));

      await expectLater(
        repo.watchEvents().toList(),
        throwsA(isA<SessionExpiredException>()),
      );
    });

    test('errors when the response is not an event stream', () async {
      final repo = buildRepo((_, __) async => http.StreamedResponse(
            Stream.value(utf8.encode('{}')),
            200,
            headers: {'content-type': 'application/json'},
          ));

      await expectLater(
        repo.watchEvents().toList(),
        throwsA(isA<Exception>().having(
            (e) => e.toString(), 'message', contains('event stream'))),
      );
    });

    test('errors when the server fails', () async {
      final repo = buildRepo(
          (_, __) async => http.StreamedResponse(const Stream.empty(), 404));

      await expectLater(
        repo.watchEvents().toList(),
        throwsA(isA<Exception>()
            .having((e) => e.toString(), 'message', contains('404'))),
      );
    });

    test('cancelling the subscription closes the response body', () async {
      final cancelled = Completer<void>();
      final body = StreamController<String>(onCancel: cancelled.complete);
      final repo = buildRepo((_, __) async => eventStream(body.stream));

      final received = Completer<PacketEvent>();
      final subscription = repo.watchEvents().listen(received.complete);
      body.add('event: resync\ndata: \n\n');
      await received.future;

      await subscription.cancel();

      await cancelled.future.timeout(const Duration(seconds: 1));
    });

    test('errors when the server goes silent for too long', () async {
      final body = StreamController<String>();
      final repo = buildRepo(
        (_, __) async => eventStream(body.stream),
        idleTimeout: const Duration(milliseconds: 200),
      );
      Object? error;
      final done = Completer<void>();
      repo.watchEvents().listen(
            (_) {},
            onError: (Object e) => error = e,
            onDone: done.complete,
          );

      // Heartbeats keep the connection alive past the timeout.
      for (var i = 0; i < 4; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 100));
        body.add(': heartbeat\n\n');
      }
      expect(error, isNull);

      await done.future.timeout(const Duration(seconds: 2));
      expect(error, isA<TimeoutException>());
    });
  });
}
