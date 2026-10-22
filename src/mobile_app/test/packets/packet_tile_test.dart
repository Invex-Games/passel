import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/clamped_text.dart';
import 'package:passel_mobileapp/packets/widgets/file_tile.dart';
import 'package:passel_mobileapp/packets/widgets/packet_list.dart';
import 'package:passel_mobileapp/packets/widgets/packet_tile.dart';
import 'package:passel_mobileapp/packets/widgets/standard_tile.dart';

import '../fakes.dart';

/// The plugin channels the tile actions end up on.  The plugins are not
/// registered in a widget test, so their Dart side talks to these mocks.
const _urlLauncherChannel = MethodChannel('plugins.flutter.io/url_launcher');
const _shareChannel = MethodChannel('dev.fluttercommunity.plus/share');

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  /// URLs the tile asked the system to open, in order.
  late List<String?> launched;

  /// What the system answers a launch with; `null` makes it fail outright.
  late bool? launchResult;

  /// Text copied to the clipboard, in order.
  late List<String?> copied;

  /// Text handed to the share sheet, in order.
  late List<String?> shared;

  String? argument(MethodCall call, String name) =>
      (call.arguments as Map<Object?, Object?>)[name] as String?;

  setUp(() {
    launched = [];
    launchResult = true;
    copied = [];
    shared = [];
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(_urlLauncherChannel, (call) async {
      launched.add(argument(call, 'url'));
      final result = launchResult;
      if (result == null) throw PlatformException(code: 'ACTIVITY_NOT_FOUND');
      return result;
    });
    messenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'Clipboard.setData') copied.add(argument(call, 'text'));
      return null;
    });
    messenger.setMockMethodCallHandler(_shareChannel, (call) async {
      shared.add(argument(call, 'text'));
      return 'dev.fluttercommunity.plus/share/success';
    });
  });

  tearDown(() {
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(_urlLauncherChannel, null);
    messenger.setMockMethodCallHandler(SystemChannels.platform, null);
    messenger.setMockMethodCallHandler(_shareChannel, null);
  });

  Future<void> pumpTile(
    WidgetTester tester,
    Packet packet, {
    VoidCallback? onDelete,
  }) =>
      tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: PacketTile(packet: packet, index: 0, onDelete: onDelete),
          ),
        ),
      ));

  /// A row of the copy or share chooser, by its leading icon.
  Finder chooserRow(IconData icon) =>
      find.ancestor(of: find.byIcon(icon), matching: find.byType(ListTile));

  const note = Packet(id: 'n', type: PacketType.text, payload: 'a note');
  const titled = Packet(
    id: 't',
    type: PacketType.text,
    payload: 'the body',
    title: 'the title',
  );
  const link = Packet(
    id: 'l',
    type: PacketType.link,
    payload: 'https://example.com/page',
  );
  const file = Packet(
    id: 'f',
    type: PacketType.file,
    payload: 'parcel-1',
    title: 'report.pdf',
    contentType: 'application/pdf',
  );

  group('a note', () {
    testWidgets('shows its body with share and copy actions', (tester) async {
      await pumpTile(tester, note);

      expect(find.byType(StandardTile), findsOneWidget);
      expect(find.byIcon(Icons.text_snippet), findsOneWidget);
      expect(find.text('Note'), findsOneWidget);
      expect(find.text('a note'), findsOneWidget);
      expect(find.byTooltip('Share'), findsOneWidget);
      expect(find.byTooltip('Copy'), findsOneWidget);
      expect(find.byTooltip('Delete'), findsNothing);
    });

    testWidgets('offers deletion only when given a callback', (tester) async {
      var deletes = 0;
      await pumpTile(tester, note, onDelete: () => deletes++);

      await tester.tap(find.byTooltip('Delete'));

      expect(deletes, 1);
    });

    testWidgets('copies its body to the clipboard and confirms it',
        (tester) async {
      await pumpTile(tester, note);

      await tester.tap(find.byTooltip('Copy'));
      await tester.pump();

      expect(copied, ['a note']);
      expect(find.text('Copied to clipboard'), findsOneWidget);
    });

    testWidgets('shares its body', (tester) async {
      await pumpTile(tester, note);

      await tester.tap(find.byTooltip('Share'));
      await tester.pump();

      expect(shared, ['a note']);
    });
  });

  group('a titled note', () {
    testWidgets('asks whether to copy the title, the body or both',
        (tester) async {
      await pumpTile(tester, titled);

      await tester.tap(find.byTooltip('Copy'));
      await tester.pumpAndSettle();
      expect(find.byType(ListTile), findsNWidgets(3));
      expect(copied, isEmpty);

      await tester.tap(chooserRow(Icons.title));
      await tester.pumpAndSettle();
      expect(copied, ['the title']);
      expect(find.byType(ListTile), findsNothing);

      await tester.tap(find.byTooltip('Copy'));
      await tester.pumpAndSettle();
      await tester.tap(chooserRow(Icons.notes));
      await tester.pumpAndSettle();
      expect(copied, ['the title', 'the body']);

      await tester.tap(find.byTooltip('Copy'));
      await tester.pumpAndSettle();
      await tester.tap(chooserRow(Icons.copy_all));
      await tester.pumpAndSettle();
      expect(copied, ['the title', 'the body', 'the title\nthe body']);
    });

    testWidgets('asks whether to share the title, the body or both',
        (tester) async {
      await pumpTile(tester, titled);

      await tester.tap(find.byTooltip('Share'));
      await tester.pumpAndSettle();
      expect(find.byType(ListTile), findsNWidgets(3));
      expect(shared, isEmpty);

      await tester.tap(chooserRow(Icons.title));
      await tester.pumpAndSettle();
      expect(shared, ['the title']);
      expect(find.byType(ListTile), findsNothing);

      await tester.tap(find.byTooltip('Share'));
      await tester.pumpAndSettle();
      await tester.tap(chooserRow(Icons.notes));
      await tester.pumpAndSettle();
      expect(shared, ['the title', 'the body']);

      await tester.tap(find.byTooltip('Share'));
      await tester.pumpAndSettle();
      // The header has a share icon too; only the chooser row is a ListTile.
      await tester.tap(chooserRow(Icons.share));
      await tester.pumpAndSettle();
      expect(shared, ['the title', 'the body', 'the title\nthe body']);
    });
  });

  group('a link', () {
    testWidgets('is underlined and opens in the browser when tapped',
        (tester) async {
      await pumpTile(tester, link);

      expect(find.byIcon(Icons.link), findsOneWidget);
      expect(find.text('Link'), findsOneWidget);
      final url = tester.widget<Text>(find.text(link.payload));
      expect(url.style?.decoration, TextDecoration.underline);
      expect(url.maxLines, 1);

      await tester.tap(find.text(link.payload));
      await tester.pump();

      expect(launched, [link.payload]);
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('says so when no app opens it', (tester) async {
      launchResult = false;
      await pumpTile(tester, link);

      await tester.tap(find.text(link.payload));
      await tester.pump();

      expect(launched, [link.payload]);
      expect(find.text('Couldn\'t open this link'), findsOneWidget);
    });

    testWidgets('says so when the system refuses to open it', (tester) async {
      launchResult = null;
      await pumpTile(tester, link);

      await tester.tap(find.text(link.payload));
      await tester.pump();

      expect(launched, [link.payload]);
      expect(find.text('Couldn\'t open this link'), findsOneWidget);
    });

    testWidgets('is plain text when it is not a web address', (tester) async {
      const payload = 'javascript:alert(1)';
      await pumpTile(
        tester,
        const Packet(id: 'j', type: PacketType.link, payload: payload),
      );

      final text = tester.widget<Text>(find.text(payload));
      expect(text.style?.decoration, isNot(TextDecoration.underline));

      await tester.tap(find.text(payload));
      await tester.pump();

      expect(launched, isEmpty);
    });
  });

  group('a file', () {
    testWidgets('is a placeholder named after the file, with no share or copy',
        (tester) async {
      await pumpTile(tester, file, onDelete: () {});

      expect(find.byType(FileTile), findsOneWidget);
      expect(find.byIcon(Icons.insert_drive_file), findsOneWidget);
      expect(find.text('report.pdf'), findsOneWidget);
      expect(find.text('Files aren\'t supported yet.'), findsOneWidget);
      expect(find.byTooltip('Share'), findsNothing);
      expect(find.byTooltip('Copy'), findsNothing);
      expect(find.byTooltip('Delete'), findsOneWidget);
    });

    testWidgets('shows an image icon for an unnamed image', (tester) async {
      await pumpTile(
        tester,
        const Packet(
          id: 'i',
          type: PacketType.file,
          payload: 'parcel-2',
          contentType: 'image/jpeg',
          isImage: true,
        ),
      );

      expect(find.byIcon(Icons.image), findsOneWidget);
      expect(find.byIcon(Icons.insert_drive_file), findsNothing);
      expect(find.text('File'), findsOneWidget);
    });
  });

  testWidgets('a long note stays expanded when the feed moves it',
      (tester) async {
    final long = List.generate(20, (i) => 'line ${i + 1}').join('\n');
    final repo = FakePacketRepository()
      ..onFetch = (_) async => PacketsState(packets: [
            Packet(id: 'long', type: PacketType.text, payload: long),
            textPacket('b'),
          ]);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: const MaterialApp(home: Scaffold(body: PacketList())),
    ));
    await tester.pumpAndSettle();
    final container =
        ProviderScope.containerOf(tester.element(find.byType(PacketList)));
    Text body() => tester.widget<Text>(find.text(long));
    final toggle = find.descendant(
      of: find.byType(ClampedText),
      matching: find.byType(TextButton),
    );

    expect(body().maxLines, StandardTile.noteMaxLines);
    await tester.tap(toggle);
    await tester.pumpAndSettle();
    expect(body().maxLines, isNull);

    await container
        .read(packetsProvider.notifier)
        .applyCreated(textPacket('new'));
    await tester.pumpAndSettle();

    expect(find.text('body new'), findsOneWidget);
    expect(body().maxLines, isNull);
  });
}
