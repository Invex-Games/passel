import 'packet.dart';

/// Parses [text] as an absolute `http`/`https` URL with a non-empty host.
///
/// Returns `null` for anything else (other schemes such as `javascript:` or
/// `intent:`, relative paths, text containing whitespace, malformed input),
/// so only web links are ever treated as links or launched.
Uri? tryParseHttpUrl(String text) {
  final trimmed = text.trim();
  if (trimmed.isEmpty || trimmed.contains(RegExp(r'\s'))) return null;

  final uri = Uri.tryParse(trimmed);
  if (uri == null) return null;
  if (uri.scheme != 'http' && uri.scheme != 'https') return null;
  if (uri.host.isEmpty) return null;
  return uri;
}

/// Decides whether user input should be pushed as a link or a text packet.
PacketType packetTypeForInput(String text) =>
    tryParseHttpUrl(text) != null ? PacketType.link : PacketType.text;
