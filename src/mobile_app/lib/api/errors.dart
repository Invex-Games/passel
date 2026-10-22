import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;

// ─── Exceptions ──────────────────────────────────────────────────────────────

/// Thrown when the user's session can't be used or refreshed (no token, or
/// the server keeps rejecting it).  The UI should prompt the user to sign in.
class SessionExpiredException implements Exception {
  const SessionExpiredException([
    this.message = 'Session expired. Please sign in again.',
  ]);

  final String message;

  @override
  String toString() => message;
}

/// A request the server answered with a failure status.
///
/// [title], [detail] and [errors] come from the problem details body
/// (RFC 9457, `application/problem+json`) when the server sent one.  The
/// API describes every failure that way: a `detail` sentence, or for
/// validation failures the messages keyed by field in [errors].
class ApiException implements Exception {
  const ApiException({
    required this.status,
    this.title,
    this.detail,
    this.errors = const {},
    this.context,
  });

  /// Reads a failure [response].  A body that isn't JSON, or doesn't parse,
  /// gives an exception that carries only the status.
  factory ApiException.fromResponse(
    http.Response response, {
    String? context,
  }) {
    final status = response.statusCode;
    final contentType = response.headers['content-type'] ?? '';
    if (!contentType.contains('json')) {
      return ApiException(status: status, context: context);
    }
    try {
      final body = jsonDecode(response.body);
      if (body is! Map<String, dynamic>) {
        return ApiException(status: status, context: context);
      }
      return ApiException(
        status: status,
        title: _sentence(body['title']),
        detail: _sentence(body['detail']),
        errors: _fieldErrors(body['errors']),
        context: context,
      );
    } on FormatException {
      return ApiException(status: status, context: context);
    }
  }

  /// The HTTP status code.
  final int status;

  /// A short summary of the problem type, e.g. `Bad Request`.
  final String? title;

  /// An explanation of this failure, written for people.
  final String? detail;

  /// Validation messages keyed by the request field they are about.
  final Map<String, List<String>> errors;

  /// What was being done, e.g. `Fetch packets`; for logs and debugging.
  final String? context;

  /// Every validation message, in field order.
  List<String> get fieldErrors =>
      [for (final messages in errors.values) ...messages];

  @override
  String toString() {
    final summary =
        detail ?? (fieldErrors.isNotEmpty ? fieldErrors.join(' ') : title);
    return '${context ?? 'Request'} failed with HTTP $status'
        '${summary != null ? ': $summary' : ''}';
  }

  static String? _sentence(Object? value) {
    if (value is! String) return null;
    final trimmed = value.trim();
    return trimmed.isEmpty ? null : trimmed;
  }

  static Map<String, List<String>> _fieldErrors(Object? raw) {
    if (raw is! Map<String, dynamic>) return const {};
    final errors = <String, List<String>>{};
    raw.forEach((field, messages) {
      final list = (messages is List<dynamic> ? messages : [messages])
          .map(_sentence)
          .whereType<String>()
          .toList();
      if (list.isNotEmpty) errors[field] = List.unmodifiable(list);
    });
    return Map.unmodifiable(errors);
  }
}

/// The server could not be reached: no connection, a DNS or TLS failure, a
/// reset connection or a timeout.  [cause] is the underlying error.
class NetworkException implements Exception {
  const NetworkException(this.cause);

  final Object cause;

  @override
  String toString() => 'NetworkException: $cause';
}

/// Sign-in failed for a reason worth telling the user; [message] is written
/// for them.  [cause] is the underlying error, for logs.
class SignInException implements Exception {
  const SignInException(this.message, [this.cause]);

  final String message;
  final Object? cause;

  @override
  String toString() => cause == null ? message : '$message ($cause)';
}

// ─── Network errors ──────────────────────────────────────────────────────────

/// Whether [error] means the server could not be reached, as opposed to it
/// answering with a failure: the errors `package:http` and `dart:io` throw
/// for transport failures, and [NetworkException] itself.
bool isNetworkError(Object error) =>
    error is NetworkException ||
    error is SocketException ||
    error is TimeoutException ||
    error is http.ClientException;

/// Runs [action], rethrowing transport failures as [NetworkException] so
/// callers can tell "could not reach the server" from everything else.
Future<T> wrapNetworkErrors<T>(Future<T> Function() action) async {
  try {
    return await action();
  } on NetworkException {
    rethrow;
  } catch (e, s) {
    if (isNetworkError(e)) Error.throwWithStackTrace(NetworkException(e), s);
    rethrow;
  }
}

// ─── User-facing text ────────────────────────────────────────────────────────

/// Short, user-facing text for [error], for snackbars and error rows.
///
/// Client errors (4xx) use what the server said: the `detail` sentence or
/// the validation messages.  Everything else gets a generic sentence, so
/// no exception text or server internals reach the screen.
String describeError(Object error) {
  if (error is SessionExpiredException) {
    return 'Your session has expired. Please sign in again.';
  }
  if (error is SignInException) return error.message;
  if (isNetworkError(error)) {
    return 'Could not reach Passel. Check your connection and try again.';
  }
  if (error is ApiException) return _describeApiError(error);
  return 'Something went wrong. Please try again.';
}

String _describeApiError(ApiException error) {
  final status = error.status;
  if (status >= 400 && status < 500) {
    final detail = error.detail;
    if (detail != null) return detail;
    if (error.fieldErrors.isNotEmpty) return error.fieldErrors.join(' ');
    return switch (status) {
      413 => 'That is too large to send.',
      429 => 'Too many requests. Wait a moment and try again.',
      _ => 'Something went wrong (HTTP $status). Please try again.',
    };
  }
  return 'Passel is having trouble right now. Please try again later.';
}
