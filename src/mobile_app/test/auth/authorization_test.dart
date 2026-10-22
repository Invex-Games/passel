import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/auth/data/authorization.dart';

void main() {
  test('a Google ID token is sent as a Bearer token', () {
    expect(authorizationHeader('eyJhbGciOi.payload.sig'),
        'Bearer eyJhbGciOi.payload.sig');
    expect(devUserOf('eyJhbGciOi.payload.sig'), isNull);
  });

  test('a development token is sent with the Dev scheme', () {
    final token = devToken('alice');

    expect(token, startsWith(devTokenPrefix));
    expect(devUserOf(token), 'alice');
    expect(authorizationHeader(token), 'Dev alice');
  });
}
