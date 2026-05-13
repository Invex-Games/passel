import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:http/http.dart' as http;

import '../../auth/auth_helpers.dart';
import '../../config/app_config.dart';

// ─── Parcel service ──────────────────────────────────────────────────────────

final parcelServiceProvider = Provider<ParcelService>(ParcelService.new);

/// Handles uploading parcels (binary blobs) via the API.
///
/// Image downloading and caching is handled by `cached_network_image_ce`
/// via the [parcelCacheManagerProvider]. This service retains only the
/// [upload] method needed by the push-packet flow.
class ParcelService {
  ParcelService(this._ref);

  final Ref _ref;

  /// Uploads [file] as a new parcel and returns the server-assigned parcel ID.
  Future<String> upload(File file) async {
    final token = await requireToken(_ref);
    final client = _ref.read(apiClientProvider);
    final uri = AppConfig.apiUri(AppConfig.parcelsEndpoint);

    final request = http.MultipartRequest('POST', uri)
      ..headers['Authorization'] = 'Bearer $token'
      ..files.add(await http.MultipartFile.fromPath('file', file.path));

    final streamed = await client.send(request);
    final response = await http.Response.fromStream(streamed);

    validateResponse(response, context: 'Parcel upload');

    final decoded = jsonDecode(response.body) as Map<String, dynamic>;
    final id = decoded['id'] as String?;
    if (id == null || id.isEmpty) {
      throw Exception('Parcel upload response missing id');
    }
    return id;
  }
}
