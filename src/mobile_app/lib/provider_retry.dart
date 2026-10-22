/// Opts a provider out of Riverpod's automatic retry.
///
/// Since Riverpod 3 a provider whose build fails is rebuilt by default, ten
/// times with an exponential backoff from 200 ms to 6.4 s, and reports
/// loading rather than the error while it waits.  This app surfaces failures
/// itself and lets the user decide: the start-up page shows a sign-in error,
/// the feed shows the error with a Retry button, and the live stream has its
/// own reconnect schedule.  Providers that can fail pass this as `retry` so
/// they keep failing fast, as they did before.
Duration? noRetry(int retryCount, Object error) => null;
