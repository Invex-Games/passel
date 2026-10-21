import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/packets/data/server_sent_events.dart';

void main() {
  /// Decodes [chunks] (each sent as its own UTF-8 chunk) into
  /// `(event, data)` pairs.
  Future<List<(String, String)>> decode(List<String> chunks) =>
      decodeServerSentEvents(Stream.fromIterable(chunks.map(utf8.encode)))
          .map((e) => (e.event, e.data))
          .toList();

  group('decodeServerSentEvents', () {
    test('decodes event types and data', () async {
      expect(
        await decode(['event: resync\ndata: \n\nevent: packet-created\n'
            'data: {"id":"1"}\n\n']),
        [('resync', ''), ('packet-created', '{"id":"1"}')],
      );
    });

    test('joins multi-line data and defaults the type to "message"',
        () async {
      expect(
        await decode(['data: first\ndata: second\n\n']),
        [('message', 'first\nsecond')],
      );
    });

    test('ignores comments, unknown fields and events without data',
        () async {
      expect(
        await decode([
          ': keep-alive\n\n',
          'event: ping\n\n',
          'id: 7\nretry: 1000\nevent: resync\ndata: \n\n',
        ]),
        [('resync', '')],
      );
    });

    test('handles CRLF line endings and fields without a space', () async {
      expect(
        await decode(['event:resync\r\ndata:x\r\n\r\n']),
        [('resync', 'x')],
      );
    });

    test('reassembles lines and characters split across chunks', () async {
      final bytes = utf8.encode('event: packet-created\ndata: héllo\n\n');
      final split = bytes.indexOf(0xc3) + 1; // inside the two-byte "é"

      final events = await decodeServerSentEvents(Stream.fromIterable([
        bytes.sublist(0, 5),
        bytes.sublist(5, split),
        bytes.sublist(split),
      ])).map((e) => (e.event, e.data)).toList();

      expect(events, [('packet-created', 'héllo')]);
    });

    test('drops an incomplete event when the stream ends', () async {
      expect(await decode(['event: resync\ndata: \n']), isEmpty);
    });
  });
}
