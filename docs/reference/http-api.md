# HTTP API

When the API runs locally in development, the interactive reference is at `https://localhost:7030/scalar`. This page summarises the contract.

Every endpoint below requires `Authorization: Bearer <Google ID token>`. When the API runs in Development it also accepts `Authorization: Dev <user-id>`; see [Run without Google](../getting-started/running-locally.md#run-without-google). Browsers may call the API only from the origins in `Cors:AllowedOrigins`, which is where the web app is served from ([Configuration](configuration.md#web-api)). Requests may pass `?api-version=1`; when it's omitted, version 1 is assumed. Errors use `application/problem+json`.

## Limits

Each endpoint is rate limited per user, over a sliding window of one minute. The defaults are 30 creates, 120 list or delete requests, and 10 event-stream connections per minute; see [Configuration](configuration.md#web-api). A request over the limit gets **429 Too Many Requests** with a `Retry-After` header (in seconds) and a problem body.

Each user also has a quota: by default 5,000 packets and 50 MB of stored packets. A create at the packet limit gets **429**, and one that would pass the byte limit gets **413 Content Too Large**; the problem `detail` says which, and that deleting packets frees the quota. Neither has a `Retry-After`, because only a delete changes the outcome.

## Packets

Packets are a tagged union with a `$type` discriminator. `$type` doesn't have to be the first property.

| `$type` | Fields |
|---|---|
| `Link` | `id`, `createdAt`, `name` (nullable), `url`, `preview` (nullable; see [Link previews](#link-previews)) |
| `Text` | `id`, `createdAt`, `title` (nullable), `body` |
| `File` | `id`, `createdAt`, `fileName`, `contentType`, `parcelId`, `isImage`. Returned only; creating file packets isn't supported yet. |

`id` is an opaque string (a Sqids-encoded Guid). `createdAt` is when the packet was created, as an ISO 8601 timestamp in UTC (for example `2026-10-22T11:03:08.1234567+00:00`); it is returned, never sent.

### Link previews

After a link is created, the API fetches its page in the background and reads what the page says about itself: the Open Graph tags first (`og:title`, `og:description`, `og:image`), the Twitter card tags next, and the `<title>` and `<meta name="description">` last. What it finds is stored on the link and returned as its `preview`:

```json
{ "title": "Example Domain", "description": "This domain is for use in illustrative examples in documents.", "imageUrl": "https://example.com/images/og.png", "fetchedAt": "2026-10-22T11:03:10.1234567+00:00" }
```

`title`, `description` and `imageUrl` are each nullable, but never all three at once; `fetchedAt` is when the page was read. The title keeps at most 256 characters and the description 512. `imageUrl` is the page's image as an absolute `http` or `https` URL; the API has not fetched it, so clients load it as they would any third-party image.

`preview` is `null` on the create response and in the `packet-created` event, because the page is fetched after the create has returned. When the fetch finds something, the link is announced again as a `packet-updated` event, and the list shows the preview from then on. When the page gives nothing, is not HTML, cannot be reached within a few attempts, or is on a private network the API refuses to fetch from, `preview` stays `null` for good: nothing is stored or announced about the failure. Links created before previews existed have none.

### `GET /api/packets`

Returns the caller's packets, newest first, 50 per page.

| Query | Description |
|---|---|
| `cursor` | The `nextCursor` from the previous page. An invalid cursor returns 400. |

```json
{ "packets": [ { "$type": "Link", "id": "…", "createdAt": "2026-10-22T11:03:08.1234567+00:00", "name": "Example", "url": "https://example.com" } ], "nextCursor": "…" }
```

`nextCursor` is `null` on the last page.

### `POST /api/packets`

Creates a packet and returns it, with its `id` and `createdAt`, as **200 OK**. Nullable fields must be sent explicitly, even as `null`.

```json
{ "$type": "Text", "title": null, "body": "Hello from my laptop" }
```

| Rule | Limit |
|---|---|
| `name` and `title` | At most 256 characters |
| `url` | Absolute `http` or `https` URL, at most 2048 characters |
| `body` | Not blank, at most 65,536 characters |
| Request body | At most 256 KB |

A `File` packet returns 400. Validation failures return a validation problem keyed by field. A create that would exceed the caller's quota returns 429 or 413; see [Limits](#limits).

### `DELETE /api/packets/{id}`

Returns 204 and erases the packet; nothing of it is kept. Returns 404 if the id is malformed, the packet doesn't exist, or it belongs to someone else.

### `GET /api/packets/events`

A `text/event-stream` of the caller's changes:

| Event | Data |
|---|---|
| `resync` | Empty. Sent first, and whenever events may have been missed. Fetch the list again. |
| `packet-created` | The packet, in the same shape as a list item |
| `packet-updated` | The packet, in the same shape as `packet-created`, after a change to it: today, a link whose [preview](#link-previews) arrived. A client that doesn't handle the event loses nothing by ignoring it; the next list shows the change. |
| `packet-deleted` | `{ "id": "…" }` |
| `ping` | Heartbeat, after 20 seconds without other events |

There are no event ids. To recover after a disconnect, reconnect and handle the `resync`. The server may close a stream at any time, for example on shutdown or when the client falls behind. The first event carries a `retry:` field, a reconnection delay between 2 and 6 seconds chosen per stream, so clients that honour it (such as `EventSource`) reconnect spread out after a restart. Other clients should reconnect with jittered backoff.

A stream also ends when the token that opened it would stop validating (its `exp` plus five minutes of clock skew, so about an hour for a Google ID token) and after a day at most. Reconnect with a fresh token. A user may hold five streams open per API instance; opening a sixth closes the oldest. When an instance has no room for another stream at all, the request gets **503 Service Unavailable** with `Retry-After`.

## Other endpoints

| Endpoint | Auth | Description |
|---|---|---|
| `GET /health` | None | Readiness: every health check |
| `GET /alive` | None | Liveness |
| `GET /auth/login`, `/auth/logout`, `/auth/me` | Development only | Cookie login for Scalar. `/auth/login` answers 503 when the Google client isn't configured. |
