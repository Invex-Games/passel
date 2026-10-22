import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';

import '../provider_retry.dart';

/// The version of the installed app, as the platform reports it.
///
/// On Android [version] is the `versionName` and [buildNumber] the
/// `versionCode`, both from the pubspec `version` line, which
/// `atom BuildMobileAppAndroid` stamps from GitVersion and the build time.
class AppVersion {
  const AppVersion({required this.version, required this.buildNumber});

  final String version;
  final String buildNumber;

  /// `1.2.0 (42)`, or just the version when there is no build number.
  String get label => buildNumber.isEmpty ? version : '$version ($buildNumber)';
}

/// Reads the [AppVersion] from the platform once, for the settings page.
///
/// Fails rather than retries when the platform has no answer (in a widget
/// test, say), so the page can show that instead of waiting.
final appVersionProvider = FutureProvider<AppVersion>(
  (ref) async {
    final info = await PackageInfo.fromPlatform();
    return AppVersion(version: info.version, buildNumber: info.buildNumber);
  },
  retry: noRetry,
);
