import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'package:passel_mobileapp/api/errors.dart';

void main() {
  http.Response problem(
    int status,
    Map<String, Object?> body, {
    String contentType = 'application/problem+json',
  }) =>
      http.Response(
        jsonEncode(body),
        status,
        headers: {'content-type': contentType},
      );

  group('ApiException.fromResponse', () {
    test('reads the title and detail of a problem body', () {
      final e = ApiException.fromResponse(
        problem(400, {
          'type': 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          'title': 'Bad Request',
          'status': 400,
          'detail': 'The cursor is invalid.',
        }),
        context: 'Fetch packets',
      );

      expect(e.status, 400);
      expect(e.title, 'Bad Request');
      expect(e.detail, 'The cursor is invalid.');
      expect(e.errors, isEmpty);
      expect(
        e.toString(),
        'Fetch packets failed with HTTP 400: The cursor is invalid.',
      );
    });

    test('reads validation errors keyed by field', () {
      final e = ApiException.fromResponse(problem(400, {
        'title': 'One or more validation errors occurred.',
        'status': 400,
        'errors': {
          'url': [
            'Url must be an absolute http or https URL of at most 2048 '
                'characters.'
          ],
          'name': ['Name must be at most 256 characters.'],
        },
      }));

      expect(e.title, 'One or more validation errors occurred.');
      expect(e.detail, isNull);
      expect(e.errors.keys, ['url', 'name']);
      expect(e.errors['name'], ['Name must be at most 256 characters.']);
      expect(e.fieldErrors, hasLength(2));
      expect(
        e.toString(),
        'Request failed with HTTP 400: Url must be an absolute http or https '
        'URL of at most 2048 characters. Name must be at most 256 characters.',
      );
    });

    test('accepts plain application/json too', () {
      final e = ApiException.fromResponse(problem(
        409,
        {'title': 'Conflict', 'detail': 'Already deleted.'},
        contentType: 'application/json; charset=utf-8',
      ));

      expect(e.detail, 'Already deleted.');
    });

    test('carries only the status for a non-JSON body', () {
      final e = ApiException.fromResponse(http.Response(
        '<html>Bad gateway</html>',
        502,
        headers: {'content-type': 'text/html'},
      ));

      expect(e.status, 502);
      expect(e.title, isNull);
      expect(e.detail, isNull);
      expect(e.errors, isEmpty);
      expect(e.toString(), 'Request failed with HTTP 502');
    });

    test('carries only the status when the body does not parse', () {
      const json = {'content-type': 'application/problem+json'};

      expect(
        ApiException.fromResponse(http.Response('not json {', 500, headers: json))
            .detail,
        isNull,
      );
      expect(
        ApiException.fromResponse(http.Response('[1, 2]', 500, headers: json))
            .title,
        isNull,
      );
      expect(ApiException.fromResponse(http.Response('', 500)).status, 500);
    });

    test('drops blank fields and messages that are not text', () {
      final e = ApiException.fromResponse(problem(400, {
        'title': '  ',
        'detail': '',
        'errors': {
          'body': ['', 5, ' Body is required. '],
          'title': <String>[],
          'name': 'Name is too long.',
          'url': null,
        },
      }));

      expect(e.title, isNull);
      expect(e.detail, isNull);
      expect(e.errors, {
        'body': ['Body is required.'],
        'name': ['Name is too long.'],
      });
    });
  });

  group('wrapNetworkErrors', () {
    test('returns the result', () async {
      expect(await wrapNetworkErrors(() async => 42), 42);
    });

    test('rethrows transport failures as NetworkException', () async {
      final causes = <Object>[
        const SocketException('Network unreachable'),
        TimeoutException('idle', const Duration(seconds: 45)),
        http.ClientException('Connection reset'),
      ];
      for (final cause in causes) {
        await expectLater(
          wrapNetworkErrors(() async => throw cause),
          throwsA(isA<NetworkException>()
              .having((e) => e.cause, 'cause', same(cause))),
        );
      }
    });

    test('leaves other errors alone', () async {
      await expectLater(
        wrapNetworkErrors(() async => throw const SessionExpiredException()),
        throwsA(isA<SessionExpiredException>()),
      );
      await expectLater(
        wrapNetworkErrors(() async => throw const ApiException(status: 500)),
        throwsA(isA<ApiException>()),
      );
      await expectLater(
        wrapNetworkErrors(() async => throw const NetworkException('x')),
        throwsA(isA<NetworkException>().having((e) => e.cause, 'cause', 'x')),
      );
    });
  });

  group('describeError', () {
    const connection =
        'Could not reach Passel. Check your connection and try again.';
    const generic = 'Something went wrong. Please try again.';
    const serverTrouble =
        'Passel is having trouble right now. Please try again later.';

    test('an expired session asks to sign in again', () {
      const expected = 'Your session has expired. Please sign in again.';
      expect(describeError(const SessionExpiredException()), expected);
      expect(
        describeError(const SessionExpiredException(
            'Fetch packets was rejected (HTTP 401).')),
        expected,
      );
    });

    test('transport failures, wrapped or not, ask to check the connection',
        () {
      expect(
        describeError(const NetworkException(SocketException('x'))),
        connection,
      );
      expect(describeError(const SocketException('x')), connection);
      expect(describeError(TimeoutException('x')), connection);
      expect(describeError(http.ClientException('x')), connection);
    });

    test('client errors use the detail the server sent', () {
      expect(
        describeError(const ApiException(
          status: 400,
          title: 'Bad Request',
          detail: 'The cursor is invalid.',
        )),
        'The cursor is invalid.',
      );
      expect(
        describeError(const ApiException(
          status: 400,
          title: 'Bad Request',
          detail: 'File packets are not supported in the current version.',
        )),
        'File packets are not supported in the current version.',
      );
    });

    test('validation failures list the field messages', () {
      expect(
        describeError(const ApiException(
          status: 400,
          title: 'One or more validation errors occurred.',
          errors: {
            'name': ['Name must be at most 256 characters.'],
            'url': ['Url must be an absolute http or https URL.'],
          },
        )),
        'Name must be at most 256 characters. '
        'Url must be an absolute http or https URL.',
      );
    });

    test('client errors without an explanation get a short sentence', () {
      expect(
        describeError(const ApiException(status: 404, title: 'Not Found')),
        'Something went wrong (HTTP 404). Please try again.',
      );
      expect(
        describeError(const ApiException(status: 413)),
        'That is too large to send.',
      );
      expect(
        describeError(const ApiException(status: 429)),
        'Too many requests. Wait a moment and try again.',
      );
    });

    test('server errors never show what the server said', () {
      expect(
        describeError(const ApiException(
          status: 500,
          title: 'Internal Server Error',
          detail: 'NullReferenceException at PacketStoreService.Create',
        )),
        serverTrouble,
      );
      expect(describeError(const ApiException(status: 503)), serverTrouble);
    });

    test('sign-in failures pass their message through', () {
      expect(
        describeError(const SignInException(
            'Google Sign-In is not available on this device.')),
        'Google Sign-In is not available on this device.',
      );
    });

    test('anything else is generic', () {
      expect(describeError(Exception('Push packet failed with HTTP 500')),
          generic);
      expect(describeError(const FormatException('Unexpected character')),
          generic);
      expect(describeError(StateError('bad')), generic);
      expect(describeError('a string'), generic);
    });
  });
}
