import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/data/packet_repository.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';

import 'fakes.dart';

// The wire contract, as the API's integration tests write it.  `contracts/`
// at the repository root holds sample payloads the API serialized
// (`fixtures/`) and the feed sequences every client must agree on
// (`feed-scenarios.json`).  Both go through the real parsers and the real
// [PacketsNotifier] here, so a change to the wire shape or to the feed rules
// on either side fails this suite.  See contracts/README.md.

/// `contracts/` above the package: `flutter test` runs in the package
/// directory, so the repository root is found by walking up from it.
Directory findContractsDir() {
  var dir = Directory.current.absolute;
  while (true) {
    final candidate = Directory('${dir.path}${Platform.pathSeparator}contracts');
    final marker =
        File('${candidate.path}${Platform.pathSeparator}feed-scenarios.json');
    if (marker.existsSync()) return candidate;
    final parent = dir.parent;
    if (parent.path == dir.path) {
      throw StateError('No contracts/ folder above ${Directory.current.path}; '
          'run the tests from a checkout of the repository.');
    }
    dir = parent;
  }
}

/// What [Packet.fromJson] must make of a wire packet: the one place this
/// suite spells the field mapping out.
void expectParsedFrom(Packet packet, Map<String, dynamic> raw) {
  expect(packet.id, raw['id']);
  switch (raw[r'$type']) {
    case 'Link':
      expect(packet.type, PacketType.link);
      expect(packet.title, raw['name']);
      expect(packet.payload, raw['url']);
      expect(packet.contentType, isNull);
      expect(packet.isImage, isFalse);
    case 'Text':
      expect(packet.type, PacketType.text);
      expect(packet.title, raw['title']);
      expect(packet.payload, raw['body']);
      expect(packet.contentType, isNull);
      expect(packet.isImage, isFalse);
    case 'File':
      expect(packet.type, PacketType.file);
      expect(packet.title, raw['fileName']);
      expect(packet.payload, raw['parcelId']);
      expect(packet.contentType, raw['contentType']);
      expect(packet.isImage, raw['isImage']);
    default:
      fail('This suite has no mapping for a ${raw[r'$type']} packet');
  }
}

/// The events a [PacketRepository] reads from an event stream whose body is
/// [sse]: the real stream decoding and event mapping, as in production.
Future<List<PacketEvent>> eventsFrom(String sse) {
  final repo = PacketRepository(
    client: MockClient.streaming((_, __) async => http.StreamedResponse(
          Stream.value(utf8.encode(sse)),
          200,
          headers: const {'content-type': 'text/event-stream'},
        )),
    tokenProvider: ({bool forceRefresh = false}) async => 'token',
    cache: InMemoryPacketCache(),
    baseUrl: 'https://test.example.com',
  );
  return repo.watchEvents().toList();
}

void main() {
  final contractsDir = findContractsDir();
  final fixturesDir =
      Directory('${contractsDir.path}${Platform.pathSeparator}fixtures');

  Object? readJson(Directory dir, String name) =>
      jsonDecode(File('${dir.path}${Platform.pathSeparator}$name')
          .readAsStringSync());

  // ─── Fixtures ──────────────────────────────────────────────────────────────

  final fixtureChecks = <String, Future<void> Function(Object? json)>{
    'list-page.json': (json) async {
      final page = json as Map<String, dynamic>;
      final raw =
          (page['packets'] as List<dynamic>).cast<Map<String, dynamic>>();
      final packets = Packet.listFromJson(raw);

      // Every packet parses, in order, to what its fields say; and the page
      // shows every kind there is.
      expect(packets, hasLength(raw.length));
      for (var i = 0; i < raw.length; i++) {
        expectParsedFrom(packets[i], raw[i]);
      }
      expect(packets.map((p) => p.type).toSet(), PacketType.values.toSet());
      expect(page['nextCursor'], isA<String>());
    },
    'create-response.json': (json) async {
      final raw = json as Map<String, dynamic>;
      expectParsedFrom(Packet.fromJson(raw), raw);
    },
    'packet-created-event.json': (json) async {
      final raw = json as Map<String, dynamic>;
      final events =
          await eventsFrom('event: packet-created\ndata: ${jsonEncode(raw)}\n\n');
      final created = events.single;
      expect(created, isA<PacketCreatedEvent>());
      expectParsedFrom((created as PacketCreatedEvent).packet, raw);
    },
    'packet-deleted-event.json': (json) async {
      final raw = json as Map<String, dynamic>;
      expect(raw['id'], isA<String>());
      final events =
          await eventsFrom('event: packet-deleted\ndata: ${jsonEncode(raw)}\n\n');
      final deleted = events.single;
      expect(deleted, isA<PacketDeletedEvent>());
      expect((deleted as PacketDeletedEvent).id, raw['id']);
    },
    'packet-updated-event.json': (json) async {
      // A link with its preview, in the shape of a created packet.  The app
      // ignores the event until it shows previews (roadmap 2.D1); the packet
      // in it still parses, and the preview field is simply not read.
      final raw = json as Map<String, dynamic>;
      expectParsedFrom(Packet.fromJson(raw), raw);
      final events =
          await eventsFrom('event: packet-updated\ndata: ${jsonEncode(raw)}\n\n');
      expect(events, isEmpty);
    },
  };

  group('contract fixtures', () {
    test('every fixture the API writes has a check here', () {
      final names = fixturesDir
          .listSync()
          .whereType<File>()
          .map((file) => file.uri.pathSegments.last)
          .toList()
        ..sort();
      expect(names, fixtureChecks.keys.toList()..sort());
    });

    for (final entry in fixtureChecks.entries) {
      test('${entry.key} parses with the real parser', () async {
        await entry.value(readJson(fixturesDir, entry.key));
      });
    }
  });

  // ─── Feed scenarios ────────────────────────────────────────────────────────

  final scenarioFile =
      readJson(contractsDir, 'feed-scenarios.json') as Map<String, dynamic>;
  final catalogue = scenarioFile['packets'] as Map<String, dynamic>;

  Map<String, dynamic> wire(String id) {
    final packet = catalogue[id];
    if (packet == null) fail('feed-scenarios.json defines no packet $id');
    return packet as Map<String, dynamic>;
  }

  group('feed scenarios', () {
    final scenarios =
        (scenarioFile['scenarios'] as List<dynamic>).cast<Map<String, dynamic>>();

    for (final scenario in scenarios) {
      test(scenario['name'] as String, () async {
        final repo = FakePacketRepository();
        Map<String, dynamic>? serves;
        var fetches = 0;

        repo.onFetch = (cursor) async {
          final page = serves;
          serves = null;
          if (page == null) {
            fail('The feed fetched (cursor $cursor) in a step that serves '
                'nothing');
          }
          expect(cursor, page['cursor'], reason: 'the cursor asked for');
          fetches++;
          final ids = (page['packets'] as List<dynamic>).cast<String>();
          return PacketsState(
            packets: Packet.listFromJson(ids.map(wire).toList()),
            nextCursor: page['nextCursor'] as String?,
            lastSyncedAt: DateTime(2026, 10, 22),
          );
        };

        ProviderContainer? container;

        PacketsNotifier notifier() {
          final opened = container;
          if (opened == null) fail('The scenario must open the feed first');
          return opened.read(packetsProvider.notifier);
        }

        final steps =
            (scenario['steps'] as List<dynamic>).cast<Map<String, dynamic>>();

        for (final step in steps) {
          final action = step['action'] as String;
          serves = step['serves'] as Map<String, dynamic>?;
          final fetchesBefore = fetches;

          // Drives the feed the way the app does for each action; a live
          // event's packet goes through the real parser.
          switch (action) {
            case 'open':
              final opened = ProviderContainer(overrides: [
                authServiceProvider
                    .overrideWith(() => FakeAuthService(signedIn: true)),
                packetRepositoryProvider.overrideWithValue(repo),
                immediateCacheWrites(),
              ]);
              addTearDown(opened.dispose);
              await opened.read(authServiceProvider.future);
              opened.listen(packetsProvider, (_, __) {});
              await opened.read(packetsProvider.future);
              container = opened;
            case 'refresh':
              await notifier().refresh();
            case 'loadMore':
              await notifier().loadMore();
            case 'resync':
              await notifier().syncLatest();
            case 'created':
              await notifier()
                  .applyCreated(Packet.fromJson(wire(step['packet'] as String)));
            case 'deleted':
              await notifier().applyDeleted(step['id'] as String);
            case 'updated':
              // Ignored by this version of the app, so the list is left
              // alone.  Once the app handles the event, apply it here; the
              // scenarios then check that it changes no ids.
              final events = await eventsFrom('event: packet-updated\n'
                  'data: ${jsonEncode(wire(step['packet'] as String))}\n\n');
              if (events.isNotEmpty) {
                fail('The app now handles packet-updated; teach this '
                    'interpreter to apply it');
              }
            case 'hide':
              notifier().hide(step['id'] as String);
            case 'unhide':
              notifier().unhide(step['id'] as String);
            default:
              fail('feed-scenarios.json uses an action this suite does not '
                  'know: $action');
          }

          expect(fetches - fetchesBefore, step.containsKey('serves') ? 1 : 0,
              reason: 'fetches during $action');

          final state = notifier().state.requireValue;
          final expected = step['expect'] as Map<String, dynamic>;
          expect(state.packets.map((p) => p.id).toList(),
              (expected['packets'] as List<dynamic>).cast<String>(),
              reason: 'the list after $action');
          if (expected.containsKey('nextCursor')) {
            expect(state.nextCursor, expected['nextCursor'],
                reason: 'the cursor after $action');
          }
        }
      });
    }
  });
}
