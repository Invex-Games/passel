import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:passel_mobileapp/auth/auth_helpers.dart';

void main() {
  late List<http.BaseRequest> sent;
  late MockClient inner;

  setUp(() {
    sent = [];
    inner = MockClient((request) async {
      sent.add(request);
      return http.Response('{}', 200);
    });
  });

  group('ApiClient', () {
    test('never follows redirects', () async {
      await ApiClient(inner).get(Uri.parse('https://api.example.com/x'));

      expect(sent.single.followRedirects, isFalse);
    });

    test('sends no tunnel header without a token', () async {
      await ApiClient(inner, tunnelAccessToken: '')
          .get(Uri.parse('https://api.example.com/x'));

      expect(sent.single.headers.keys.map((k) => k.toLowerCase()),
          isNot(contains('x-tunnel-authorization')));
    });

    test('sends the tunnel access token with every request', () async {
      final client = ApiClient(inner, tunnelAccessToken: 'abc.def');
      final uri = Uri.parse('https://tunnel.example.com/api/packets');

      await client.get(uri, headers: {'Authorization': 'Bearer t'});
      await client.post(uri, body: '{}');
      await client.send(http.Request('GET', uri));

      expect(sent, hasLength(3));
      for (final request in sent) {
        expect(request.headers[ApiClient.tunnelAuthorizationHeader],
            'tunnel abc.def');
      }
      // Other headers are kept.
      expect(sent.first.headers['Authorization'], 'Bearer t');
    });

    test('defaults the token to the build configuration', () {
      // Tests run without dart-defines, so the default is empty.
      expect(ApiClient(inner).tunnelAccessToken, isEmpty);
    });
  });
}
