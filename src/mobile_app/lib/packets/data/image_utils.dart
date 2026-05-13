import 'dart:convert';

import 'package:flutter/foundation.dart';

/// Returns true if [bytes] look like SVG content.
bool isSvg(Uint8List bytes) {
  final head = utf8.decode(
    bytes.sublist(0, bytes.length.clamp(0, 256)),
    allowMalformed: true,
  );
  return head.contains('<svg') || head.trimLeft().startsWith('<?xml');
}

