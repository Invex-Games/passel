// ─── Packet model ────────────────────────────────────────────────────────────

enum PacketType {
  link,
  text,
  file,
}

class Packet {
  const Packet({
    required this.id,
    required this.type,
    required this.payload,
    this.title,
  });

  final String id;
  final PacketType type;
  final String payload;
  final String? title;

  /// Serialises to the API's wire format (also used for the disk cache).
  ///
  /// The `$type` discriminator must come first for the server to accept it.
  Map<String, dynamic> toJson() => {
        '\$type': _wireType(type),
        'id': id,
        ...switch (type) {
          PacketType.text => {'title': title, 'body': payload},
          PacketType.link => {'name': title, 'url': payload},
          PacketType.file => {'fileName': title, 'parcelId': payload},
        },
      };

  /// Parses a packet from the API's wire format, e.g.
  /// `{"$type": "Text", "id": "…", "title": "…", "body": "…"}`.
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
    if (payload.isEmpty) {
      throw FormatException('Packet $payloadKey is required.');
    }

    return Packet(id: id, type: type, payload: payload, title: title);
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
    this.lastSyncedAt,
  });

  final List<Packet> packets;
  final String? nextCursor;
  final bool isLoadingMore;
  final DateTime? lastSyncedAt;

  bool get hasMore => nextCursor != null;

  PacketsState copyWith({
    List<Packet>? packets,
    String? Function()? nextCursor,
    bool? isLoadingMore,
    DateTime? Function()? lastSyncedAt,
  }) {
    return PacketsState(
      packets: packets ?? this.packets,
      nextCursor: nextCursor != null ? nextCursor() : this.nextCursor,
      isLoadingMore: isLoadingMore ?? this.isLoadingMore,
      lastSyncedAt:
          lastSyncedAt != null ? lastSyncedAt() : this.lastSyncedAt,
    );
  }
}
