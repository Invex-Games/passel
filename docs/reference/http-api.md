# HTTP API

When the API runs locally in development, the interactive reference is at `https://localhost:7030/scalar`. This page summarises the contract.

Every endpoint below requires `Authorization: Bearer <Google ID token>`. When the API runs in Development it also accepts `Authorization: Dev <user-id>`; see [Run without Google](../getting-started/running-locally.md#run-without-google). Browsers may call the API only from the origins in `Cors:AllowedOrigins`, which is where the web app is served from ([Configuration](configuration.md#web-api)). Requests may pass `?api-version=1`; when it's omitted, version 1 is assumed. Errors use `application/problem+json`.

## Limits

Each endpoint is rate limited per user, over a sliding window of one minute. The defaults are 30 creates, 120 list or delete requests (the device list counts here too), 10 event-stream connections, 20 device writes (registrations, renames, removals and push token changes), 10 file uploads and discards (`parcel-uploads`), and 600 file downloads and thumbnails (`parcel-reads`) per minute; see [Configuration](configuration.md#web-api). A request over the limit gets **429 Too Many Requests** with a `Retry-After` header (in seconds) and a problem body.

Each user also has a quota: by default 5,000 packets and 50 MB of stored packets. A create at the packet limit gets **429**, and one that would pass the byte limit gets **413 Content Too Large**; the problem `detail` says which, and that deleting packets frees the quota. Neither has a `Retry-After`, because only a delete changes the outcome. In the same way, a user may have 20 devices; registering another gets **429** without a `Retry-After` until one is removed. The cap is not exact: registrations of new installations sent at the same moment are each checked against the count before any of them is stored, so a burst can take a user past it, by at most one less than the device-writes rate limit.

Uploaded files have quotas of their own, apart from the packet quota: a file may be at most 25 MB, a user's files at most 500 MB together, and a user may have 20 files uploaded but not yet sent. An upload of a larger file gets **413** ("File too large"), one that would pass the storage limit **413** ("Storage limit reached"), and one beyond the unsent files **429** ("Too many unsent files"), none with a `Retry-After`: sending, discarding or deleting files, or waiting for unsent ones to expire after an hour, is what frees the quota. Like the packet quota, these are checked before the file is stored and can be passed by uploads sent at the same moment. When file storage can't be reached, the file endpoints answer **503** with `Retry-After`, and packets, devices and the event stream keep working; see [Parcels](#parcels).

A request body the API cannot read at all (malformed JSON, a missing or unknown `$type`, an unknown enum value, a missing required field) gets **400** with a plain problem; one it can read but refuses gets **400** with a validation problem, whose `errors` are keyed by field.

## Packets

Packets are a tagged union with a `$type` discriminator. `$type` doesn't have to be the first property.

| `$type` | Fields |
|---|---|
| `Link` | `id`, `createdAt`, `name` (nullable), `url`, `preview` (nullable; see [Link previews](#link-previews)) |
| `Text` | `id`, `createdAt`, `title` (nullable), `body` |
| `File` | `id`, `createdAt`, `fileName` (nullable), `contentType`, `parcelId`, `isImage`. Created from an uploaded file; see [Parcels](#parcels). |

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

A `File` packet sends a file uploaded to [`POST /api/parcels`](#post-apiparcels), by the `parcelId` the upload answered with. `fileName` may be left out (or `null`) to keep the name the file was uploaded with, or given to send it under another; it is made safe the way an uploaded name is. The type and `isImage` always come from the upload.

```json
{ "$type": "File", "parcelId": "qrjMmYBVnK8m3coMo0hiMeN", "fileName": "screenshot.png" }
```

| Rule | Limit |
|---|---|
| `parcelId` | A file the caller uploaded, has not discarded, and has not sent yet |
| `fileName` | At most 256 characters |

A `parcelId` that isn't one of the caller's uploaded files (another user's, one still uploading, one discarded or expired, or not an id at all) gets **400** with a validation problem on `parcelId`, one message for all of them so ids can't be probed. A file that is already in a packet gets **409 Conflict** ("File already sent"): a file is sent once, so upload it again to send it twice. Two sends of the same file at the same moment get one 200 and one 409.

Validation failures return a validation problem keyed by field. A create that would exceed the caller's quota returns 429 or 413; see [Limits](#limits). A File packet counts against the packet quota for its name only; its bytes count against the file quota when uploaded.

### `DELETE /api/packets/{id}`

Returns 204 and erases the packet; nothing of it is kept. Returns 404 if the id is malformed, the packet doesn't exist, or it belongs to someone else. Deleting a File packet deletes its file with it: from that moment the file's download and thumbnail answer 404, and its bytes are erased from storage within seconds (at most five minutes, the sweep interval).

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

## Devices

A device is one installation of a client (the Android app, the extension in one browser profile, the web app in one browser) signed in as one user. Clients register on every start and may give the API a push token, which the API will use to tell the device that something arrived (push sending is not built yet). Every device endpoint lives under `/api/devices` and acts only on the caller's own devices; another user's device is answered as a missing one, with **404**.

| Field | Description |
|---|---|
| `id` | The device's public id, opaque like a packet id. Keep the id your registration returned: it is how the client tells itself apart in the list. |
| `name` | What the user calls the device, at most 64 characters |
| `platform` | `android`, `extension` or `web` |
| `createdAt` | When the installation first registered, in UTC |
| `lastSeenAt` | When it last registered, in UTC |
| `pushUpdatedAt` | When its push token was last set, in UTC, or `null` when it has none. The token itself is never returned. |

```json
{ "id": "…", "name": "Pixel 9", "platform": "android", "createdAt": "2026-11-04T09:30:00.1234567+00:00", "lastSeenAt": "2026-11-07T08:12:45.7654321+00:00", "pushUpdatedAt": null }
```

### `POST /api/devices`

Registers the installation and returns the device.

```json
{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": "Pixel 9", "platform": "android" }
```

The installation id is the client's own: generate it once per installation (a random UUID will do), keep it in the client's storage, and send it with every registration. That makes registering idempotent:

- An installation id the caller has not registered makes a new device: **201 Created**, with a `Location` of `/api/devices/{id}`.
- One the caller has registered returns the same device as **200 OK**, with `lastSeenAt` moved on. The `name` sent is ignored, because the user may have renamed the device since; it is only the name of a new device.
- One the caller has registered with another platform gets **409 Conflict**, whose `detail` names the client it is registered for.
- Registrations of one installation sent at the same moment all get the same device: one gets **201** and the rest **200**.
- The same installation id under another account is a separate device of that account. Signing out and in as someone else therefore adds a device to the second account and leaves the first account's device where it was, to be removed from its settings.
- A removed device is gone: when its installation registers again, it gets a new device with a new `id`.

| Rule | Limit |
|---|---|
| `installationId` | 16 to 128 ASCII letters, digits, `-` or `_` |
| `name` | Not blank, at most 64 characters without the whitespace around it (which is trimmed), no control characters |
| `platform` | `android`, `extension` or `web` |
| Request body | At most 16 KB, on every device endpoint |

A new device beyond the caller's device cap gets **429**; see [Limits](#limits). An installation that is registered already is never refused.

### `GET /api/devices`

Returns all of the caller's devices, in the order they registered. The list is not paged; the device cap bounds it.

```json
{ "devices": [ { "id": "…", "name": "Pixel 9", "platform": "android", "createdAt": "…", "lastSeenAt": "…", "pushUpdatedAt": "…" } ] }
```

### `PATCH /api/devices/{id}`

Renames the device and returns it. The name follows the registration rules.

```json
{ "name": "Work phone" }
```

### `DELETE /api/devices/{id}`

Returns 204 and removes the device with its push token. Returns 404 if the id is malformed, the device doesn't exist, or it belongs to someone else.

### `PUT /api/devices/{id}/push`

Gives the device its push token, replacing any it had, and returns **204**. Android devices send their Firebase Cloud Messaging registration token:

```json
{ "$type": "Fcm", "token": "…" }
```

Extension and web devices send their Web Push subscription, which is `PushSubscription.toJSON()` with the discriminator added (`expirationTime` is ignored):

```json
{ "$type": "WebPush", "endpoint": "https://fcm.googleapis.com/fcm/send/…", "keys": { "p256dh": "…", "auth": "…" } }
```

| Rule | Limit |
|---|---|
| Kind | `Fcm` on an `android` device, `WebPush` on an `extension` or `web` device; the other kind is a validation problem keyed `$type` |
| `token` | 1 to 4,096 printable ASCII characters, no spaces |
| `endpoint` | An absolute `https` URL of at most 2,048 characters, on a host name (not an IP address) and the default port, without credentials |
| `keys.p256dh` | Base64url of a 65-byte uncompressed P-256 public key |
| `keys.auth` | Base64url of a 16-byte secret |

Send the token again whenever the client starts or the platform gives it a new one; `pushUpdatedAt` then shows how fresh it is. A push token belongs to one device at a time. When a token (an FCM token, or a subscription's endpoint) is set on a device while another device holds it, under this account or any other, the other device loses it. That is what happens when one installation signs in as another account: its token follows the new account, and the old account's device stops receiving pushes. Nothing in the response says that a token moved.

A body without `$type`, such as `PushSubscription.toJSON()` sent as it is, gets **400**.

### `DELETE /api/devices/{id}/push`

Removes the device's push token and returns **204**, also when it had none. Returns 404 for a device the caller doesn't have.

### Conflicts

A registration, a rename, or setting or removing a push token, that keeps colliding with other requests changing the same device at the same moment gets **409 Conflict** with a problem saying to send it again. That is safe: each of these requests can be repeated without a different outcome. Registrations of one installation never collide with each other this way.

## Parcels

A parcel is an uploaded file. Sending a file takes two requests: upload it to `POST /api/parcels`, then create a `File` packet with the `parcelId` the upload answered with ([`POST /api/packets`](#post-apipackets)). Every parcel endpoint lives under `/api/parcels` and acts only on the caller's own files; another user's file is answered as a missing one, with **404**. A file is the packet's from the moment it is sent: it lives as long as its packet, and deleting the packet deletes it. A file uploaded but not sent within an hour is deleted.

| Field | Description |
|---|---|
| `parcelId` | The file's public id, opaque like a packet id, and the `parcelId` of the File packet that sends it |
| `fileName` | The name the file was uploaded with, made safe (see below), or `null` when nothing of it was left |
| `contentType` | The type read from the file's first bytes: `image/jpeg`, `image/png`, `image/gif`, `image/webp`, `application/pdf`, or `application/octet-stream` for anything else. The type the client declared is never used. |
| `size` | The file's size in bytes |
| `isImage` | Whether the file decoded as a JPEG, PNG, GIF or WebP image. Only an image has a thumbnail. |
| `width`, `height` | The image's size in pixels as it is displayed, after its EXIF orientation; `null` when it is not an image |

```json
{ "parcelId": "qrjMmYBVnK8m3coMo0hiMeN", "fileName": "screenshot.png", "contentType": "image/png", "size": 48213, "isImage": true, "width": 1280, "height": 720 }
```

An image larger than 40 megapixels, or more than 16,384 pixels on a side, is not decoded: it is stored with its type but as `isImage: false`, with no thumbnail and no size. So is one that fails to decode.

### `POST /api/parcels`

Uploads one file as `multipart/form-data`, with one part named `file` that carries a file name, which is what a browser's `FormData` and Flutter's `MultipartRequest` send:

```
POST /api/parcels
Content-Type: multipart/form-data; boundary=x

--x
Content-Disposition: form-data; name="file"; filename="screenshot.png"
Content-Type: image/png

<the bytes>
--x--
```

The part's `Content-Type` is ignored. The file name is made safe before it is kept: only what follows the last `/` or `\` is kept, control characters and the characters that reorder text (such as U+202E) are removed, it is normalised (NFC), and spaces and dots are trimmed from both ends. A name with nothing left is `null`.

Returns the file as **201 Created**, with a `Location` of `/api/parcels/{parcelId}`.

| Status | When |
|---|---|
| **201** | The file is stored |
| **400** | A validation problem on `file`: no `multipart/form-data` boundary, no part named `file` with a file name, an empty file, more than one part, or a body that isn't valid multipart. On `fileName`: the safe name is longer than 256 characters. |
| **413** | The file is over 25 MB ("File too large"; a body that says it is longer is refused before it is read), or it would take the caller's files past 500 MB ("Storage limit reached") |
| **415** | The body is not `multipart/form-data` |
| **429** | The caller has 20 files uploaded but not sent ("Too many unsent files", no `Retry-After`), or the `parcel-uploads` rate limit (with `Retry-After`) |
| **503** | Too many uploads at once on the server ("File storage is busy", `Retry-After: 5`), file storage can't be reached ("File storage unavailable", `Retry-After: 30`), or the upload took so long that it was discarded ("Upload discarded"); send it again |

A body sent without a `Content-Length` (chunked) is accepted, and counts as 25 MB against the storage limit until it is stored.

### `GET /api/parcels/{id}`

Returns the file's bytes as they were uploaded, with its sniffed `Content-Type`.

| Header | Value |
|---|---|
| `ETag` | The SHA-256 of the bytes, in lower-case hex, quoted. Send it as `If-None-Match` and an unchanged file (they never change) gets **304 Not Modified**. |
| `Last-Modified` | When the file was uploaded |
| `Accept-Ranges` | `bytes`: a `Range` request gets **206 Partial Content**, and one outside the file **416 Range Not Satisfiable**. `If-Range` is honoured. |
| `Content-Disposition` | `inline` for an image that decoded and for a PDF, `attachment` for anything else, so an uploaded page or script is saved rather than opened. Always with the file's name, as `filename*` in UTF-8 and as an ASCII `filename` for clients that only read that (`file.png` and so on when the file has no name). |

Returns 404 if the id is malformed, the file doesn't exist, belongs to someone else, is still uploading, or has been discarded, expired or deleted with its packet. A browser that opens a PDF under `Content-Security-Policy: sandbox` may refuse to show it; the file is then downloaded instead.

### `GET /api/parcels/{id}/thumbnail`

Returns the image's thumbnail: a WebP of at most 512 pixels on its longest side (smaller images keep their size), the first frame of an animated image, turned upright by its EXIF orientation. Its `ETag` is the file's with the thumbnail size added, and `If-None-Match` gets 304; it has no ranges. Returns 404 as the download does, and for a file that has no thumbnail (`isImage: false`).

### `DELETE /api/parcels/{id}`

Discards a file that hasn't been sent and returns **204**. It is unavailable at once, and its bytes are erased from storage within seconds. A file that is in a packet gets **409 Conflict** ("Delete the item instead"): delete its packet instead, which deletes the file with it. Returns 404 for a file the caller doesn't have. Discards count against the `parcel-uploads` rate limit.

### Headers and caching

Every answer of a parcel endpoint, its problems and 404s included, carries `Content-Security-Policy: sandbox; default-src 'none'`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`, so nothing uploaded runs as a page on the API's origin or is read as another type. A download or thumbnail (200, 206 or 304) is `Cache-Control: private, max-age=31536000, immutable` with `Vary: Authorization`, since a file never changes; every other answer is `Cache-Control: no-store`. File bodies are never compressed, so `Content-Length` and ranges always hold. The 401, the rate limit's 429 and the 415 for a body that isn't multipart are answered before the endpoint runs, and carry none of these.

From a browser, the web app's origin may send `Range` and `If-None-Match` and read `ETag`, `Content-Disposition`, `Content-Range`, `Accept-Ranges`, `Content-Length` and `Location`; see [Configuration](configuration.md#web-api).

### Privacy

The original is stored and returned byte for byte, so a photo keeps its EXIF metadata, GPS position included, for whoever downloads it with the owner's token. Only the thumbnail is stripped: it carries no EXIF, XMP or colour profile. Delete a file (or its packet) to erase it: the storage keeps no deleted copy, neither a soft-deleted blob nor an earlier version.

## Other endpoints

| Endpoint | Auth | Description |
|---|---|---|
| `GET /health` | None | Readiness: every health check. File storage is not one of them, so a storage outage leaves the API ready. |
| `GET /alive` | None | Liveness |
| `GET /auth/login`, `/auth/logout`, `/auth/me` | Development only | Cookie login for Scalar. `/auth/login` answers 503 when the Google client isn't configured. |
