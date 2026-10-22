import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/widgets/standard_tile.dart';

void main() {
  Future<void> pumpTile(WidgetTester tester, Packet packet) =>
      tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: StandardTile(packet: packet, index: 0),
          ),
        ),
      ));

  Text bodyText(WidgetTester tester, String body) =>
      tester.widget<Text>(find.text(body));

  String lines(int count) =>
      List.generate(count, (i) => 'line ${i + 1}').join('\n');

  group('StandardTile', () {
    testWidgets('clamps a long note to eight lines and expands it in place',
        (tester) async {
      final body = lines(20);
      await pumpTile(
        tester,
        Packet(id: 'n', type: PacketType.text, payload: body),
      );

      expect(bodyText(tester, body).maxLines, StandardTile.noteMaxLines);
      expect(bodyText(tester, body).overflow, TextOverflow.ellipsis);
      expect(find.text('Show more'), findsOneWidget);

      await tester.tap(find.text('Show more'));
      await tester.pumpAndSettle();

      expect(bodyText(tester, body).maxLines, isNull);
      expect(find.text('Show less'), findsOneWidget);
      expect(find.text('Show more'), findsNothing);

      await tester.tap(find.text('Show less'));
      await tester.pumpAndSettle();

      expect(bodyText(tester, body).maxLines, StandardTile.noteMaxLines);
      expect(find.text('Show more'), findsOneWidget);
    });

    testWidgets('shows a short note whole, without the toggle',
        (tester) async {
      final body = lines(3);
      await pumpTile(
        tester,
        Packet(id: 'n', type: PacketType.text, payload: body),
      );

      expect(find.text(body), findsOneWidget);
      expect(bodyText(tester, body).maxLines, isNull);
      expect(find.text('Show more'), findsNothing);
    });

    testWidgets('keeps a link to a single ellipsised line', (tester) async {
      final url = 'https://learn.microsoft.com/${'segment/' * 40}page';
      await pumpTile(
        tester,
        Packet(id: 'l', type: PacketType.link, payload: url),
      );

      expect(bodyText(tester, url).maxLines, 1);
      expect(bodyText(tester, url).overflow, TextOverflow.ellipsis);
      expect(find.text('Show more'), findsNothing);
    });
  });
}
