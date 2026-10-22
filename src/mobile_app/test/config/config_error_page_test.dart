import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/config/app_config.dart';
import 'package:passel_mobileapp/config/config_error_page.dart';

void main() {
  test('missingKeys lists required values not passed at build time', () {
    // Tests run without dart-defines.
    expect(AppConfig.missingKeys, ['API_BASE_URL', 'SERVER_CLIENT_ID']);
    expect(AppConfig.invalidValues, isEmpty);
    expect(AppConfig.isValid, isFalse);
  });

  group('AppConfig.validate', () {
    Map<String, String> validate(
      String flavor, {
      String apiBaseUrl = 'https://api.example.com',
      String tunnelAccessToken = '',
      String devUser = '',
    }) =>
        AppConfig.validate(
          flavor: flavor,
          apiBaseUrl: apiBaseUrl,
          tunnelAccessToken: tunnelAccessToken,
          devUser: devUser,
        );

    test('accepts https in either flavor', () {
      expect(validate('development'), isEmpty);
      expect(validate('production'), isEmpty);
      expect(
        validate('production', apiBaseUrl: 'https://api.passel.link'),
        isEmpty,
      );
    });

    test('accepts http to the USB and emulator hosts in development', () {
      for (final url in [
        'http://localhost:5218',
        'http://127.0.0.1:5218',
        'http://10.0.2.2:5218',
        'http://LOCALHOST:5218',
      ]) {
        expect(validate('development', apiBaseUrl: url), isEmpty,
            reason: url);
      }
    });

    test('rejects http to any other host in development', () {
      final problems =
          validate('development', apiBaseUrl: 'http://192.168.1.20:5218');

      expect(problems.keys, ['API_BASE_URL']);
      expect(problems['API_BASE_URL'], contains('192.168.1.20 needs https://'));
      expect(problems['API_BASE_URL'], contains('localhost, 127.0.0.1, 10.0.2.2'));
    });

    test('rejects http everywhere in production', () {
      for (final url in [
        'http://localhost:5218',
        'http://10.0.2.2:5218',
        'http://api.passel.link',
      ]) {
        final problems = validate('production', apiBaseUrl: url);

        expect(problems.keys, ['API_BASE_URL'], reason: url);
        expect(
          problems['API_BASE_URL'],
          'must be an https:// URL in the production flavor',
          reason: url,
        );
      }
    });

    test('rejects a URL without an http or https scheme', () {
      for (final url in ['localhost:5218', 'api.example.com', 'ftp://x']) {
        expect(validate('development', apiBaseUrl: url).keys, ['API_BASE_URL'],
            reason: url);
        expect(validate('production', apiBaseUrl: url).keys, ['API_BASE_URL'],
            reason: url);
      }
    });

    test('leaves a missing API_BASE_URL to missingKeys', () {
      expect(validate('production', apiBaseUrl: ''), isEmpty);
    });

    test('allows a tunnel token in development only', () {
      expect(validate('development', tunnelAccessToken: 'token'), isEmpty);

      final problems = validate('production', tunnelAccessToken: 'token');

      expect(problems.keys, ['TUNNEL_ACCESS_TOKEN']);
      expect(problems['TUNNEL_ACCESS_TOKEN'], contains('production flavor'));
    });

    test('allows a development user in development only', () {
      expect(validate('development', devUser: 'alice'), isEmpty);

      final problems = validate('production', devUser: 'alice');

      expect(problems.keys, ['DEV_USER']);
      expect(problems['DEV_USER'], contains('production flavor'));
    });

    test('rejects a development user the API would refuse', () {
      for (final user in ['al ice', 'alice!', 'a' * 256]) {
        final problems = validate('development', devUser: user);

        expect(problems.keys, ['DEV_USER'], reason: user);
        expect(problems['DEV_USER'], contains('1 to 255'), reason: user);
      }
      expect(validate('development', devUser: 'dec.smith+test@example.com'),
          isEmpty);
    });

    test('reports every problem at once', () {
      final problems = validate(
        'production',
        apiBaseUrl: 'http://localhost:5218',
        tunnelAccessToken: 'token',
        devUser: 'alice',
      );

      expect(problems.keys, ['API_BASE_URL', 'TUNNEL_ACCESS_TOKEN', 'DEV_USER']);
    });
  });

  testWidgets('ConfigErrorPage names the missing keys and the fix',
      (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: ConfigErrorPage(missingKeys: ['SERVER_CLIENT_ID']),
    ));

    expect(find.text('Missing configuration'), findsOneWidget);
    expect(find.text('SERVER_CLIENT_ID'), findsOneWidget);
    expect(find.textContaining('values it can\'t use'), findsNothing);
    expect(
      find.text(
          'flutter run --flavor development --dart-define-from-file=.env'),
      findsOneWidget,
    );
  });

  testWidgets('ConfigErrorPage names invalid values with their reasons',
      (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: ConfigErrorPage(
        invalidValues: {
          'API_BASE_URL': 'must be an https:// URL in the production flavor',
        },
      ),
    ));

    expect(find.text('Invalid configuration'), findsOneWidget);
    expect(find.text('This build was compiled without:'), findsNothing);
    expect(find.text('API_BASE_URL'), findsOneWidget);
    expect(
      find.text('must be an https:// URL in the production flavor'),
      findsOneWidget,
    );
  });

  testWidgets('ConfigErrorPage shows missing and invalid values together',
      (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: ConfigErrorPage(
        missingKeys: ['SERVER_CLIENT_ID'],
        invalidValues: {'TUNNEL_ACCESS_TOKEN': 'must not be set'},
      ),
    ));

    expect(find.text('Missing configuration'), findsOneWidget);
    expect(find.text('SERVER_CLIENT_ID'), findsOneWidget);
    expect(find.text('TUNNEL_ACCESS_TOKEN'), findsOneWidget);
    expect(find.text('must not be set'), findsOneWidget);
  });
}
