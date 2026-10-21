import 'dart:async';
import 'dart:convert';

// ─── Server-sent events ──────────────────────────────────────────────────────

/// A single event from a `text/event-stream` response.
class ServerSentEvent {
  const ServerSentEvent({required this.event, required this.data});

  /// The `event:` field, or `message` when the server didn't send one.
  final String event;

  /// The `data:` lines, joined with `\n`.
  final String data;
}

/// Decodes a `text/event-stream` body into events, following the parsing
/// rules of the HTML Server-Sent Events spec: comments (`:` lines) and
/// unknown fields are ignored, and an event without `data:` is not emitted.
///
/// Cancelling the returned stream cancels [body] straight away (unlike an
/// `async*` generator, which would wait for the next line to arrive).
Stream<ServerSentEvent> decodeServerSentEvents(Stream<List<int>> body) {
  var event = '';
  final data = <String>[];

  void handleLine(String line, EventSink<ServerSentEvent> sink) {
    if (line.isEmpty) {
      if (data.isNotEmpty) {
        sink.add(ServerSentEvent(
          event: event.isEmpty ? 'message' : event,
          data: data.join('\n'),
        ));
      }
      event = '';
      data.clear();
      return;
    }

    if (line.startsWith(':')) return;

    final colon = line.indexOf(':');
    final field = colon == -1 ? line : line.substring(0, colon);
    var value = colon == -1 ? '' : line.substring(colon + 1);
    if (value.startsWith(' ')) value = value.substring(1);

    switch (field) {
      case 'event':
        event = value;
      case 'data':
        data.add(value);
    }
  }

  return body
      .transform(utf8.decoder)
      .transform(const LineSplitter())
      .transform(StreamTransformer.fromHandlers(handleData: handleLine));
}
