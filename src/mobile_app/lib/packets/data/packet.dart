// ─── Packet model ────────────────────────────────────────────────────────────

enum PacketType {
  link,
  text,

  /// File packets aren't supported by the server yet; they are parsed so a
  /// stray one doesn't break the feed, and rendered as a placeholder.
  file,
}

class Packet {
  const Packet({
    required this.id,
    required this.type,
    required this.payload,
    this.title,
    this.contentType,
    this.isImage = false,
  });

  final String id;
  final PacketType type;

  /// Text body, link URL, or (for files) the parcel id — may be empty for
  /// file packets.
  final String payload;
  final String? title;

  /// MIME type of a file packet's content (file packets only).
  final String? contentType;

  /// Whether a file packet holds an image (file packets only).
  final bool isImage;

  /// Serialises to the API's wire format (also used for the disk cache).
  ///
  /// The `$type` discriminator must come first for the server to accept it.
  Map<String, dynamic> toJson() => {
        '\$type': _wireType(type),
        'id': id,
        ...switch (type) {
          PacketType.text => {'title': title, 'body': payload},
          PacketType.link => {'name': title, 'url': payload},
          PacketType.file => {
              'fileName': title,
              'parcelId': payload,
              'contentType': contentType,
              'isImage': isImage,
            },
        },
      };

  /// Parses a packet from the API's wire format, e.g.
  /// `{"$type": "Text", "id": "…", "title": "…", "body": "…"}`.
  ///
  /// Throws [FormatException] for unknown types or missing required fields.
  /// File packets only require an id, since they are display-only.
  factory Packet.fromJson(Map<String, dynamic> json) {
    final id = json['id']?.toString() ?? '';
    final typeValue = json['\$type']?.toString();

    final type = switch (typeValue) {
      'Link' => PacketType.link,
      'Text' => PacketType.text,
      'File' => PacketType.file,
      _ => throw FormatException('Unsupported packet type: $typeValue'),
    };

    final (payloadKey, titleKey) = switch (type) {
      PacketType.text => ('body', 'title'),
      PacketType.link => ('url', 'name'),
      PacketType.file => ('parcelId', 'fileName'),
    };

    final payload = json[payloadKey]?.toString() ?? '';
    final title = json[titleKey]?.toString();

    if (id.isEmpty) {
      throw const FormatException('Packet id is required.');
    }
    if (payload.isEmpty && type != PacketType.file) {
      throw FormatException('Packet $payloadKey is required.');
    }

    return Packet(
      id: id,
      type: type,
      payload: payload,
      title: title,
      contentType:
          type == PacketType.file ? json['contentType']?.toString() : null,
      isImage: type == PacketType.file && json['isImage'] == true,
    );
  }

  /// Parses a list of wire-format packets, skipping entries that are not
  /// objects or fail [Packet.fromJson] (e.g. a packet type this version of
  /// the app doesn't know yet) instead of failing the whole page.
  static List<Packet> listFromJson(List<dynamic> rawList) {
    final packets = <Packet>[];
    for (final raw in rawList) {
      if (raw is! Map<String, dynamic>) continue;
      try {
        packets.add(Packet.fromJson(raw));
      } on FormatException {
        // Skip unsupported / malformed packet.
      }
    }
    return List.unmodifiable(packets);
  }

  /// Builds a request body for creating a new packet.
  ///
  /// Only text and link packets can be created at the moment.
  static Map<String, dynamic> createRequestJson({
    required PacketType type,
    required String payload,
    String? title,
  }) =>
      switch (type) {
        PacketType.text => {'\$type': 'Text', 'title': title, 'body': payload},
        PacketType.link => {'\$type': 'Link', 'name': title, 'url': payload},
        PacketType.file =>
          throw UnsupportedError('File packets cannot be created yet.'),
      };

  static String _wireType(PacketType type) => switch (type) {
        PacketType.link => 'Link',
        PacketType.text => 'Text',
        PacketType.file => 'File',
      };
}

/// Returns [existing] followed by the packets of [incoming] whose ids are not
/// already present (and without duplicates within [incoming]).
List<Packet> mergePackets(List<Packet> existing, List<Packet> incoming) {
  final seen = <String>{};
  final merged = <Packet>[];
  for (final packet in [...existing, ...incoming]) {
    if (seen.add(packet.id)) merged.add(packet);
  }
  return List.unmodifiable(merged);
}

// ─── Live events ──────────────────────────────────────────────────────────────

/// An event from the live packet stream (`GET /api/packets/events`).
sealed class PacketEvent {
  const PacketEvent();
}

/// The stream is connected, or events may have been missed.  Packets fetched
/// after this can't miss any created later, since those arrive on the stream.
class PacketsResyncEvent extends PacketEvent {
  const PacketsResyncEvent();
}

/// A packet was created (by this or another device).
class PacketCreatedEvent extends PacketEvent {
  const PacketCreatedEvent(this.packet);

  final Packet packet;
}

// ─── Packets state ────────────────────────────────────────────────────────────

/// Holds the current list of packets plus the cursor for the next page.
///
/// Lives alongside the model so that [PacketRepository] and [PacketCache] can
/// use it without importing the Riverpod provider layer.
class PacketsState {
  const PacketsState({
    this.packets = const [],
    this.nextCursor,
    this.isLoadingMore = false,
    this.loadMoreError,
    this.lastSyncedAt,
  });

  final List<Packet> packets;
  final String? nextCursor;
  final bool isLoadingMore;

  /// The error from the last failed "load more" (not persisted).  While set,
  /// older pages are not fetched automatically — the user has to retry.
  final Object? loadMoreError;
  final DateTime? lastSyncedAt;

  bool get hasMore => nextCursor != null;

  PacketsState copyWith({
    List<Packet>? packets,
    String? Function()? nextCursor,
    bool? isLoadingMore,
    Object? Function()? loadMoreError,
    DateTime? Function()? lastSyncedAt,
  }) {
    return PacketsState(
      packets: packets ?? this.packets,
      nextCursor: nextCursor != null ? nextCursor() : this.nextCursor,
      isLoadingMore: isLoadingMore ?? this.isLoadingMore,
      loadMoreError:
          loadMoreError != null ? loadMoreError() : this.loadMoreError,
      lastSyncedAt:
          lastSyncedAt != null ? lastSyncedAt() : this.lastSyncedAt,
    );
  }
}
