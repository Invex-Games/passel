# Web API

`src/webapi` holds four projects:

| Project | Purpose |
|---|---|
| `Invex.Passel.WebApi` | The API |
| `Invex.Passel.WebApi.Tests` | Unit tests (xUnit v3 on Microsoft.Testing.Platform) |
| `Invex.Passel.AppHost` | The Aspire AppHost that runs the local environment |
| `Invex.Passel.ServiceDefaults` | OpenTelemetry, health checks, service discovery and HTTP resilience, shared Aspire-style |

## Request pipeline

`Program.cs` adds the service defaults and the web defaults (`Extensions/HostWebExtensions.cs`). Those cover JSON options, ProblemDetails, compression, authentication, rate limiting and API versioning, plus the edge: a Kestrel connection limit, forwarded headers when a proxy is declared, host filtering, and HSTS outside Development. There is no CORS, because neither current client needs it. It then maps the packet endpoints (`Endpoints/EndpointMapper.cs`). In development it also maps OpenAPI, Scalar and the browser login endpoints (`Extensions/HostDevExtensions.cs`).

Every packet endpoint lives under `/api/packets`, requires authentication and is API version 1. The user id is the Google `sub` claim. See [HTTP API](../reference/http-api.md) for the endpoints themselves.

## Abuse controls

Each packet endpoint opts into one of three rate-limiting policies (`Extensions/RateLimitingExtensions.cs`): creates, reads (the list and delete endpoints) and event-stream connections. The limiter runs after authentication so requests are counted per user; requests that fail authentication are counted per remote address instead. Rejections are 429 problems with `Retry-After`. The limits come from the `RateLimits` configuration section; see [Configuration](../reference/configuration.md#web-api).

Before a create, `PacketQuotaService` reads the caller's usage with one aggregate over the snapshot table (`count(*)` and the summed `pg_column_size` of the documents, through the leading member of the index on `UserId` and `Id`), and `PacketQuota.Exceeded` compares it with the `Quotas` configuration. A user at the packet limit gets 429, a packet that would pass the byte limit gets 413, and both messages say to delete packets. Two creates that race can both pass the check, so the quotas are approximate by a packet.

## Storage: Marten event sourcing

Packets are stored with [Marten](https://martendb.io/) on PostgreSQL.

- Each packet is an event stream keyed by a version 7 `Guid`. The events are `TextPacketCreated`, `LinkPacketCreated`, `FilePacketCreated` and `PacketDeleted`.
- `PacketAggregate` is kept as an **inline snapshot**: a document written in the same transaction as the event. Reads query the snapshot documents. `PacketDeleted` removes the document.
- `PacketStoreService` handles creating and deleting packets. Deleting checks ownership first. A missing packet and someone else's packet both return 404, so ids can't be probed.
- Snapshot documents carry one composite computed index on `UserId` and `Id`, which serves the feed query. See [The feed index](#the-feed-index).

File packets exist in the model, but nothing creates them yet: the API rejects them with 400 and there is no file storage.

### The feed index

`GET /api/packets` runs `where UserId = ? and Id < ? order by Id desc limit 51`. With the index declared as `.Index([p => p.UserId, p => p.Id])`, Marten emits the identity member as the `id` column rather than a JSON expression, so the query becomes a backward range scan of at most 51 index entries however many packets the user has:

```sql
CREATE INDEX mt_doc_packetaggregate_idx_user_id_id ON public.mt_doc_packetaggregate USING btree ((data ->> 'UserId'), (id));
```

`Program.cs` sets the name; the one Marten derives runs the members together as `user_idid`. The single-column index on `UserId` that the API used to declare is gone, because the leading member of this one serves the same lookups, including the quota aggregate.

Measured on PostgreSQL 18.3 with 15,021 packets seeded through the API (one user with 5,001, fifty users with 200 each), `EXPLAIN (ANALYZE, BUFFERS)` of the exact statements Marten emits, warm, after `ANALYZE`:

| Query | Index on `UserId` only | Index on `UserId`, `Id` |
|---|---|---|
| First page, user with 200 packets | Index Scan Backward on the primary key, filtering on the JSON expression: 7,322 rows discarded, 7,399 buffers, 0.89 ms | Index Scan Backward on the composite index: 53 buffers, 0.03 ms |
| Cursor page, same user | The same walk from the cursor: 2,551 rows discarded, 2,608 buffers, 0.35 ms | Index Cond on both members: 50 buffers, 0.03 ms |
| First and cursor page, user with 5,001 packets | Primary-key walk: 53 buffers, 0.02 to 0.03 ms | Unchanged. A third of the table is this user's, so the walk finds 51 rows at once and the planner keeps it |
| Quota aggregate, user with 200 packets | Bitmap scan on the index: 172 buffers, 0.07 ms | Bitmap scan on the composite index: 173 buffers, 0.07 ms |

The primary-key walk reads every newer packet of every other user until it has 51 of the caller's, so it gets worse with every user added; the composite index reads 51 entries whatever the table holds. Two things to know: the planner only prefers the index once it has statistics on the expression, so run `ANALYZE` after creating it on a database that already has data (autovacuum gets there eventually), and the index is larger than the single-column one was (744 kB against 120 kB for these rows), because PostgreSQL deduplicated the old one's fifty-one distinct keys.

## Public ids

Packet ids are never exposed as raw Guids. `IdEncodingService` encodes the Guid's two 64-bit halves with [Sqids](https://sqids.org/) (minimum length 10). The alphabet comes from `Encoding:Alphabet`. Changing it invalidates every id that clients have stored.

## Live updates

`GET /api/packets/events` streams server-sent events to signed-in clients:

1. After a create or delete commits, `PacketStoreService` sends a PostgreSQL `NOTIFY` on the `passel_packet_events` channel. The payload carries the user id, packet id and the change.
2. Each API instance runs `PacketNotificationListener`, a hosted service that `LISTEN`s on that channel on its own connection. It reconnects after 5 seconds if the connection fails, and sends a keep-alive query when the connection is idle. Npgsql raises one notification per wait on the connection, so the connection loop only queues what it parses, and a dispatch loop takes everything queued at once as a batch: it loads the created packets of users with a stream open on the instance with one query, publishes per user in arrival order, and publishes deletes without a load. If the query fails, each affected user gets a `resync` instead.
3. The listener publishes to `PacketEventHub`, an in-memory hub on that instance. The hub fans events out to each user's open streams through bounded channels of 64 events.

Because every instance listens, events reach clients connected to any instance. The stream doesn't support resuming. Instead, clients reconnect, receive `resync`, and fetch `GET /api/packets` again. The server also sends `resync` when it may have missed events: after the listener reconnects, every open stream on the instance gets one, spread at random over three seconds so that the re-fetches don't all reach a database that may just have recovered at the same moment. If a stream's buffer fills, the server closes the stream rather than drop events.

A stream sends a `ping` after 20 seconds without events. The wait is a delay that completes rather than a timeout that throws, because every idle stream times out every 20 seconds. The first event of a stream carries an SSE `retry` value drawn per stream between 2 and 6 seconds, so clients that honour it (a browser `EventSource` does; the extension and the mobile app use their own jittered backoff) reconnect spread out after a restart. Event-stream requests are left out of request tracing in `ServiceDefaults`, next to the health endpoints, because a stream's request lasts up to a day and its trace would hold an activity open for all of it.

Streams are capped by the `EventStreams` configuration. The hub keeps at most five per user on an instance: subscribing a sixth removes the oldest from the hub and completes its channel, so that client's stream ends cleanly and it reconnects. Beyond the per-instance total, `Subscribe` returns `null` and the endpoint answers 503 with `Retry-After`. The subscription is registered for disposal with the response, so the hub releases it when the stream ends however it ends.

A stream never outlives the token that opened it: the endpoint reads the `exp` claim and cancels the stream at `exp` plus the bearer handler's clock skew (`PacketEndpoints.StreamLifetime`), with a ceiling of a day for principals without an expiry. The clients already reconnect with a fresh token.

## Authentication

Clients send a Google **ID token** as a bearer token. The API validates it against Google with these accepted audiences:

- `Authentication:Google:ClientId`
- `Authentication:Google:AdditionalAudiences`

In development a policy scheme picks how each request is authenticated:

- A request whose `Authorization` header is `Dev <user-id>` uses the Dev scheme (`Authentication/DevAuthenticationHandler.cs`): it signs the request in as that user, with the id as both `sub` and display name, and no Google involved. The scheme only exists in Development; see [Run without Google](../getting-started/running-locally.md#run-without-google).
- A request with any other `Authorization` header uses bearer tokens.
- Any other request uses a cookie, issued by Google OpenID Connect login, when `Authentication:Google:ClientId` and `ClientSecret` are both set. Without them there is no cookie login, so such a request gets a plain 401 from the Dev scheme.

In Development the Google settings are optional: the bearer handler is still registered, but with no client id its audience list is empty and every Google token is rejected. The OpenAPI document declares the Dev scheme so Scalar can send its header, and the Scalar banner says when Google login isn't configured.

That cookie login is what lets Scalar call authenticated endpoints. The login cookie is `Secure` and `SameSite=Strict`. The OpenID Connect correlation and nonce cookies are `Secure` and `SameSite=Lax`, which works because Google returns the code in a top-level GET (`response_mode=query`), and the nonce in the ID token is validated. Production only accepts bearer tokens, and unauthenticated requests get a plain 401.

See [Google OAuth Clients](../getting-started/google-oauth.md) for which client is which.
