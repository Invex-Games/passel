# @passel/core

The client logic Passel's TypeScript clients share, with no chrome.* or DOM APIs in it: the wire types and the parser for packets, link previews and live events (`packets.ts`, `types.ts`), the feed store with its paging, live-event and undo rules (`feed.ts`), the server-sent events parser (`sse.ts`), the mapping from a token to its `Authorization` header (`authorization.ts`), reading the claims of an ID token (`jwt.ts`), a client for the packet endpoints over `fetch` (`api.ts`) and the reconnecting event stream (`live.ts`). The web app uses all of it; the extension does not use the last three yet, since its service worker has its own.

The package is consumed from source: its `exports` point at the TypeScript files, so the Chrome extension's bundler and the web app's compile it with their own settings and nothing is built here. The tests in `test/` run with vitest and include the contract checks against `contracts/` at the repository root.

The mobile app has the same feed rules in Dart (`lib/packets/packets_provider.dart`); the feed scenarios in `contracts/feed-scenarios.json` keep the two in step.
