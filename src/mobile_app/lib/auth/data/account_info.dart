/// The account signed in through Google, as far as the app shows it.
///
/// A development user (the `DEV_USER` sign-in) has no account: it is named
/// by `AuthRepository.currentDevUser` instead.
class AccountInfo {
  const AccountInfo({required this.email, this.displayName});

  /// The account's email address; Google always provides one.
  final String email;

  /// The name on the account, or `null` when Google has none for it.
  final String? displayName;

  /// [displayName] when there is one, else [email].
  String get title => displayName ?? email;
}
