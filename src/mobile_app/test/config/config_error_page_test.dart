import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/config/app_config.dart';
import 'package:passel_mobileapp/config/config_error_page.dart';

void main() {
  test('missingKeys lists required values not passed at build time', () {
    // Tests run without dart-defines.
    expect(AppConfig.missingKeys, ['API_BASE_URL', 'SERVER_CLIENT_ID']);
  });

  testWidgets('ConfigErrorPage names the missing keys and the fix',
      (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: ConfigErrorPage(missingKeys: ['SERVER_CLIENT_ID']),
    ));

    expect(find.text('Missing configuration'), findsOneWidget);
    expect(find.text('SERVER_CLIENT_ID'), findsOneWidget);
    expect(
      find.text(
          'flutter run --flavor development --dart-define-from-file=.env'),
      findsOneWidget,
    );
  });
}
