import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/packets/data/link_utils.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';

void main() {
  group('packetTypeForInput (push link detection)', () {
    for (final input in [
      'https://example.com',
      'http://example.com/path?q=1#frag',
      '  https://example.com/trimmed  ',
      'HTTPS://EXAMPLE.COM',
      'https://localhost:8080',
    ]) {
      test('treats "$input" as a link', () {
        expect(packetTypeForInput(input), PacketType.link);
      });
    }

    for (final input in [
      'hello world',
      'example.com',
      'www.example.com',
      'mailto:someone@example.com',
      'javascript:alert(1)',
      'intent://scan/#Intent;scheme=zxing;end',
      'file:///etc/passwd',
      'ftp://example.com/file',
      'https://',
      'https:///no-host',
      'https://example.com and some more text',
      'note: remember this',
      '',
      '   ',
    ]) {
      test('treats "$input" as text', () {
        expect(packetTypeForInput(input), PacketType.text);
      });
    }
  });

  group('tryParseHttpUrl', () {
    test('returns the parsed uri for http(s) links', () {
      final uri = tryParseHttpUrl(' https://example.com/a ');
      expect(uri, isNotNull);
      expect(uri!.host, 'example.com');
      expect(uri.path, '/a');
    });

    test('returns null for malformed input without throwing', () {
      expect(tryParseHttpUrl('http://[::1'), isNull);
      expect(tryParseHttpUrl('%%%'), isNull);
    });
  });
}
