import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:passel_mobileapp/api/errors.dart';
import 'package:passel_mobileapp/auth/auth_service.dart';
import 'package:passel_mobileapp/packets/data/packet.dart';
import 'package:passel_mobileapp/packets/packets_provider.dart';
import 'package:passel_mobileapp/packets/widgets/push_packet_sheet.dart';

import '../fakes.dart';

void main() {
  late FakePacketRepository repo;

  Future<void> pumpPage(WidgetTester tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        authServiceProvider.overrideWith(() => FakeAuthService(signedIn: true)),
        packetRepositoryProvider.overrideWithValue(repo),
        immediateCacheWrites(),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (context, ref, _) {
              // Like the feed page, which has auth resolved by the time the
              // FAB can be tapped.
              ref.watch(authServiceProvider);
              return ElevatedButton(
                onPressed: () => showPushPacketSheet(context, ref),
                child: const Text('Open sheet'),
              );
            },
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
  }

  /// Opens the sheet, types [text] and pushes it.
  Future<void> push(WidgetTester tester, String text) async {
    await tester.tap(find.text('Open sheet'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), text);
    await tester.tap(find.text('Send'));
    await tester.pumpAndSettle();
  }

  setUp(() {
    repo = FakePacketRepository()..onFetch = (_) async => const PacketsState();
  });

  testWidgets('shows the validation message the server sent', (tester) async {
    repo.onPush = (_, __) async => throw const ApiException(
          status: 400,
          title: 'One or more validation errors occurred.',
          errors: {
            'body': ['Body must be non-empty and at most 65536 characters.'],
          },
        );
    await pumpPage(tester);

    await push(tester, 'hello');

    expect(
      find.text('Send failed: Body must be non-empty and at most 65536 '
          'characters.'),
      findsOneWidget,
    );
  });

  testWidgets('shows a connection message when Passel cannot be reached',
      (tester) async {
    repo.onPush = (_, __) async =>
        throw const NetworkException(SocketException('no route to host'));
    await pumpPage(tester);

    await push(tester, 'hello');

    expect(
      find.text('Send failed: Could not reach Passel. Check your connection '
          'and try again.'),
      findsOneWidget,
    );
  });

  testWidgets('prompts to sign in when the session expired', (tester) async {
    repo.onPush = (_, __) async => throw const SessionExpiredException();
    await pumpPage(tester);

    await push(tester, 'hello');

    expect(find.text('Sign in to sync'), findsOneWidget);
    expect(find.textContaining('Send failed'), findsNothing);
  });
}
