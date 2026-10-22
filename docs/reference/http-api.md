# HTTP API

When the API runs locally in development, the interactive reference is at `https://localhost:7030/scalar`. This page summarises the contract.

Every endpoint below requires `Authorization: Bearer <Google ID token>`. Requests may pass `?api-version=1`; when it's omitted, version 1 is assumed. Errors use `application/problem+json`.

## Limits

Each endpoint is rate limited per user, over a sliding window of one minute. The defaults are 30 creates, 120 list or delete requests, and 10 event-stream connections per minute; see [Configuration](configuration.md#web-api). A request over the limit gets **429 Too Many Requests** with a `Retry-After` header (in seconds) and a problem body.

Each user also has a quota: by default 5,000 packets and 50 MB of stored packets. A create at the packet limit gets **429**, and one that would pass the byte limit gets **413 Content Too Large**; the problem `detail` says which, and that deleting packets frees the quota. Neither has a `Retry-After`, because only a delete changes the outcome.

## Packets

Packets are a tagged union with a `$type` discriminator. `$type` doesn't have to be the first property.

| `$type` | Fields |
|---|---|
| `Link` | `id`, `name` (nullable), `url` |
| `Text` | `id`, `title` (nullable), `body` |
| `File` | `id`, `fileName`, `contentType`, `parcelId`, `isImage`. Returned only; creating file packets isn't supported yet. |

`id` is an opaque string (a Sqids-encoded Guid).

### `GET /api/packets`

Returns the caller's packets, newest first, 50 per page.

| Query | Description |
|---|---|
| `cursor` | The `nextCursor` from the previous page. An invalid cursor returns 400. |

```json
{ "packets": [ { "$type": "Link", "id": "…", "name": "Example", "url": "https://example.com" } ], "nextCursor": "…" }
```

`nextCursor` is `null` on the last page.

### `POST /api/packets`

Creates a packet and returns it, with its `id`, as **200 OK**. Nullable fields must be sent explicitly, even as `null`.

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

Returns 204. Returns 404 if the id is malformed, the packet doesn't exist, or it belongs to someone else.

### `GET /api/packets/events`

A `text/event-stream` of the caller's changes:

| Event | Data |
|---|---|
| `resync` | Empty. Sent first, and whenever events may have been missed. Fetch the list again. |
| `packet-created` | The packet, in the same shape as a list item |
| `packet-deleted` | `{ "id": "…" }` |
| `ping` | Heartbeat, after 20 seconds without other events |

There are no event ids. To recover after a disconnect, reconnect and handle the `resync`. The server may close a stream at any time, for example on shutdown or when the client falls behind. Clients should reconnect with backoff.

## Other endpoints

| Endpoint | Auth | Description |
|---|---|---|
| `GET /health` | None | Readiness: every health check |
| `GET /alive` | None | Liveness |
| `GET /auth/login`, `/auth/logout`, `/auth/me` | Development only | Cookie login for Scalar |
