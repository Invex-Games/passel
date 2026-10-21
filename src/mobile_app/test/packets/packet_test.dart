import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/packets/data/packet.dart';

void main() {
  group('Packet.fromJson', () {
    test('parses link packet without name', () {
      final p = Packet.fromJson({
        r'$type': 'Link',
        'id': 'abc123',
        'name': null,
        'url': 'https://example.com',
      });
      expect(p.id, 'abc123');
      expect(p.type, PacketType.link);
      expect(p.payload, 'https://example.com');
      expect(p.title, isNull);
    });

    test('parses link packet with name', () {
      final p = Packet.fromJson({
        r'$type': 'Link',
        'id': 'abc123',
        'name': 'Example',
        'url': 'https://example.com',
      });
      expect(p.type, PacketType.link);
      expect(p.title, 'Example');
    });

    test('parses text packet', () {
      final p = Packet.fromJson({
        r'$type': 'Text',
        'id': 'ghi789',
        'title': 'Greeting',
        'body': 'Hello world',
      });
      expect(p.type, PacketType.text);
      expect(p.payload, 'Hello world');
      expect(p.title, 'Greeting');
    });

    test('parses file packet', () {
      final p = Packet.fromJson({
        r'$type': 'File',
        'id': 'img001',
        'fileName': 'Screenshot',
        'contentType': 'image/png',
        'parcelId': 'abcde12345',
        'isImage': true,
      });
      expect(p.type, PacketType.file);
      expect(p.payload, equals('abcde12345'));
      expect(p.title, 'Screenshot');
      expect(p.contentType, 'image/png');
      expect(p.isImage, isTrue);
    });

    test('tolerates file packets without a parcel id', () {
      final p = Packet.fromJson({
        r'$type': 'File',
        'id': 'f1',
        'fileName': 'report.pdf',
      });
      expect(p.type, PacketType.file);
      expect(p.payload, isEmpty);
      expect(p.contentType, isNull);
      expect(p.isImage, isFalse);
    });

    test('ignores file-only fields on other packet types', () {
      final p = Packet.fromJson({
        r'$type': 'Text',
        'id': 't1',
        'body': 'hi',
        'contentType': 'text/plain',
        'isImage': true,
      });
      expect(p.contentType, isNull);
      expect(p.isImage, isFalse);
    });

    test('round-trips through toJson', () {
      for (final packet in const [
        Packet(id: 'a', type: PacketType.text, payload: 'hi', title: 'T'),
        Packet(id: 'b', type: PacketType.link, payload: 'https://x.com'),
        Packet(
          id: 'c',
          type: PacketType.file,
          payload: 'p1',
          title: 'f.png',
          contentType: 'image/png',
          isImage: true,
        ),
      ]) {
        final json = packet.toJson();
        expect(json.keys.first, r'$type');

        final parsed = Packet.fromJson(json);
        expect(parsed.id, packet.id);
        expect(parsed.type, packet.type);
        expect(parsed.payload, packet.payload);
        expect(parsed.title, packet.title);
        expect(parsed.contentType, packet.contentType);
        expect(parsed.isImage, packet.isImage);
      }
    });

    test('throws when id is missing', () {
      expect(
        () => Packet.fromJson({r'$type': 'Text', 'id': '', 'body': 'hi'}),
        throwsA(isA<FormatException>()),
      );
    });

    test('throws when body is missing', () {
      expect(
        () => Packet.fromJson({r'$type': 'Text', 'id': 'x', 'body': ''}),
        throwsA(isA<FormatException>()),
      );
    });

    test('throws on unknown type', () {
      expect(
        () => Packet.fromJson({r'$type': 'Video', 'id': 'x', 'body': 'data'}),
        throwsA(isA<FormatException>()),
      );
    });

    test('throws on old-format packets', () {
      expect(
        () => Packet.fromJson({'id': 'x', 'type': 'text', 'payload': 'hi'}),
        throwsA(isA<FormatException>()),
      );
    });
  });

  group('Packet.createRequestJson', () {
    test('builds a text request with \$type first', () {
      final json = Packet.createRequestJson(
          type: PacketType.text, payload: 'hello', title: 'T');
      expect(json.keys.first, r'$type');
      expect(json, {r'$type': 'Text', 'title': 'T', 'body': 'hello'});
    });

    test('builds a link request', () {
      final json = Packet.createRequestJson(
          type: PacketType.link, payload: 'https://x.com');
      expect(json, {r'$type': 'Link', 'name': null, 'url': 'https://x.com'});
    });

    test('rejects file packets', () {
      expect(
        () => Packet.createRequestJson(type: PacketType.file, payload: 'p'),
        throwsA(isA<UnsupportedError>()),
      );
    });
  });

  group('Packet.listFromJson', () {
    test('skips malformed and unsupported entries', () {
      final packets = Packet.listFromJson([
        {r'$type': 'Text', 'id': '1', 'body': 'ok'},
        {r'$type': 'Video', 'id': '2', 'body': 'nope'},
        {r'$type': 'Text', 'id': '', 'body': 'no id'},
        'not a map',
        {r'$type': 'Link', 'id': '3', 'url': 'https://x.com'},
      ]);
      expect(packets.map((p) => p.id), ['1', '3']);
    });
  });

  group('mergePackets', () {
    const a = Packet(id: 'a', type: PacketType.text, payload: 'a');
    const b = Packet(id: 'b', type: PacketType.text, payload: 'b');
    const c = Packet(id: 'c', type: PacketType.text, payload: 'c');
    const bUpdated = Packet(id: 'b', type: PacketType.text, payload: 'b2');

    test('appends new packets and keeps existing order', () {
      expect(mergePackets([a, b], [c]).map((p) => p.id), ['a', 'b', 'c']);
    });

    test('drops incoming duplicates, keeping the existing copy', () {
      final merged = mergePackets([a, b], [bUpdated, c]);
      expect(merged.map((p) => p.id), ['a', 'b', 'c']);
      expect(merged[1].payload, 'b');
    });

    test('de-duplicates within the incoming list', () {
      expect(mergePackets([], [a, a, b]).map((p) => p.id), ['a', 'b']);
    });
  });

  group('PacketsState', () {
    test('copyWith can set and clear loadMoreError', () {
      const state = PacketsState();
      final failed = state.copyWith(loadMoreError: () => 'boom');
      expect(failed.loadMoreError, 'boom');
      expect(failed.copyWith(isLoadingMore: true).loadMoreError, 'boom');
      expect(failed.copyWith(loadMoreError: () => null).loadMoreError, isNull);
    });

    test('lastSyncedAt defaults to null', () {
      const state = PacketsState();
      expect(state.lastSyncedAt, isNull);
    });

    test('copyWith preserves lastSyncedAt when not overridden', () {
      final now = DateTime(2026, 5, 15);
      final state = PacketsState(lastSyncedAt: now);
      final copy = state.copyWith(isLoadingMore: true);
      expect(copy.lastSyncedAt, now);
      expect(copy.isLoadingMore, isTrue);
    });

    test('copyWith can set lastSyncedAt to a new value', () {
      final old = DateTime(2026, 1, 1);
      final updated = DateTime(2026, 5, 15);
      final state = PacketsState(lastSyncedAt: old);
      final copy = state.copyWith(lastSyncedAt: () => updated);
      expect(copy.lastSyncedAt, updated);
    });

    test('copyWith can clear lastSyncedAt to null', () {
      final state = PacketsState(lastSyncedAt: DateTime(2026, 5, 15));
      final copy = state.copyWith(lastSyncedAt: () => null);
      expect(copy.lastSyncedAt, isNull);
    });
  });
}
