# Web API

`src/webapi` holds five projects:

| Project | Purpose |
|---|---|
| `Invex.Passel.WebApi` | The API |
| `Invex.Passel.WebApi.Tests` | Unit tests (xUnit v3 on Microsoft.Testing.Platform) |
| `Invex.Passel.WebApi.IntegrationTests` | Integration tests against PostgreSQL in Docker; see [Integration tests](#integration-tests) |
| `Invex.Passel.AppHost` | The Aspire AppHost that runs the local environment, and describes the Azure deployment ([Releasing and Deploying](../guides/deploying.md)) |
| `Invex.Passel.ServiceDefaults` | OpenTelemetry (to the Aspire dashboard locally, Application Insights in Azure), health checks, service discovery and HTTP resilience, shared Aspire-style |

## Request pipeline

`Program.cs` adds the service defaults and the web defaults (`Extensions/HostWebExtensions.cs`). Those cover JSON options, ProblemDetails, compression, authentication, rate limiting and API versioning, plus the edge: a Kestrel connection limit, forwarded headers when a proxy is declared, host filtering, and HSTS outside Development. There is no CORS, because neither current client needs it. It then maps the packet endpoints (`Endpoints/EndpointMapper.cs`). In development it also maps OpenAPI, Scalar and the browser login endpoints (`Extensions/HostDevExtensions.cs`), in one route group that only answers to the `localhost` host (`RequireHost`), so they are not served through the dev tunnel; the packet endpoints answer to any host.

Every packet endpoint lives under `/api/packets`, requires authentication and is API version 1. The user id is the Google `sub` claim. See [HTTP API](../reference/http-api.md) for the endpoints themselves.

## Abuse controls

Each packet endpoint opts into one of three rate-limiting policies (`Extensions/RateLimitingExtensions.cs`): creates, reads (the list and delete endpoints) and event-stream connections. The limiter runs after authentication so requests are counted per user; requests that fail authentication are counted per remote address instead. Rejections are 429 problems with `Retry-After`. The limits come from the `RateLimits` configuration section; see [Configuration](../reference/configuration.md#web-api).

Before a create, `PacketQuotaService` reads the caller's usage with one aggregate over the packet table (`count(*)` and the summed `pg_column_size` of the documents, through the leading member of the index on `UserId` and `Id`), and `PacketQuota.Exceeded` compares it with the `Quotas` configuration. A user at the packet limit gets 429, a packet that would pass the byte limit gets 413, and both messages say to delete packets. Two creates that race can both pass the check, so the quotas are approximate by a packet.

## Storage: Marten documents

Packets are stored with [Marten](https://martendb.io/) on PostgreSQL, as plain documents ([ADR 0001](../decisions/0001-packets-as-documents.md)).

- Each packet is one `Packet` document (`Domain/Packet.cs`) in the `mt_doc_packet` table: `Id`, `UserId`, `CreatedAt` and the `Content` union (`Link`, `Text` or `File`, stored with a `$type` discriminator). The id is a version 7 `Guid`, so id order is creation order; the feed and its cursor rely on that. A `Link` also carries its `Preview` once one has been fetched; see [Link previews](#link-previews).
- `PacketStoreService` handles creating and deleting packets, and attaching a link's preview: one upsert to create, one delete to remove, one update for the preview. Deleting checks ownership first. A missing packet and someone else's packet both return 404, so ids can't be probed. A delete removes the document, and nothing else keeps the content.
- The documents carry one composite computed index on `UserId` and `Id`, which serves the feed query. See [The feed index](#the-feed-index).
- In Development, Marten applies the schema at startup (`ApplyAllDatabaseChangesOnStartup`), so the table exists before the first request; the quota check reads it with plain SQL, which Marten's on-first-use creation would not cover. Everywhere else the deployment applies it; see [Schema in production](#schema-in-production).
- The data source comes from `Aspire.Azure.Npgsql` (`Extensions/HostDatabaseExtensions.cs`): a connection string with a password is used as it is, and one without signs in with a Microsoft Entra token for the managed identity, as the Azure deployment does.

File packets exist in the model, but nothing creates them yet: the API rejects them with 400 and there is no file storage.

A database from before the move still has the event-sourcing tables (`mt_doc_packetaggregate`, `mt_events`, `mt_streams`); nothing reads them, and they can be dropped. Marten does not migrate the old packets into the new table.

### Schema in production

Outside Development the API never changes the schema ([ADR 0003](../decisions/0003-hosting-on-azure.md)):

- The store uses `AutoCreate.None`, so nothing is created or altered at runtime, and the API's database role needs no more than DML.
- At startup it checks that the schema matches its configuration (`AssertDatabaseMatchesConfigurationOnStartup`) and refuses to start otherwise, so a revision whose migration didn't run never becomes ready.
- The deployment's migration job applies the schema before each new revision starts, by running the API's image with the `db-migrate` command.

`db-migrate` (`Extensions/MigrateDatabaseCommand.cs`, `Services/DatabaseMigrator.cs`) logs in as a role that may change the schema, and then:

1. Applies the configured schema with `AutoCreate.CreateOrUpdate`, which adds and changes objects, drops indexes and functions that are no longer configured, and never drops a table or a column.
2. Creates the role `passel_app` if it doesn't exist (no login), and grants it select, insert, update and delete on the tables in the store's schema, use of its sequences and execution of its functions, now and, through default privileges, on what the migration creates later.
3. If `Database:ApiRole` is set, makes that role a member of `passel_app`. If `Database:ApiRoleObjectId` is set too, it first creates the role as a Microsoft Entra principal of that managed identity (a login role carrying the `pgaadauth` security label), so the API can sign in with tokens.

Every step is idempotent, so the job runs on every deployment. The API's role can then read and write packets, listen and notify, and run the quota query, but not create, alter or drop anything; the integration tests check both sides.

`Program.cs` runs [JasperFx's command line](https://martendb.io/configuration/cli.html) when the first argument names a command, and the API otherwise. Besides `db-migrate`, `db-assert` checks a database against the configuration, `db-patch <file>` writes the statements a migration would run without running them, and `help` lists the rest. For example, against the database in `appsettings.Development.json`:

```
dotnet run --project src/webapi/Invex.Passel.WebApi -- db-patch patch.sql
```

There is no code generation step. Marten 9 removed runtime code generation, so there is nothing for `codegen write` to produce and nothing to regenerate when a document type is added. A unit test (`DatabaseSetupTests`) fails if a package ever registers code that would need generating ahead of time; that is when to add `codegen write` to the build, with a check that its committed output is current.

### The feed index

`GET /api/packets` runs `where UserId = ? and Id < ? order by Id desc limit 51`. With the index declared as `.Index([p => p.UserId, p => p.Id])`, Marten emits the identity member as the `id` column rather than a JSON expression, so the query becomes a backward range scan of at most 51 index entries however many packets the user has:

```sql
CREATE INDEX mt_doc_packet_idx_user_id_id ON public.mt_doc_packet USING btree ((data ->> 'UserId'), (id));
```

`Program.cs` sets the name; the one Marten derives runs the members together as `user_idid`. The single-column index on `UserId` that the API used to declare is gone, because the leading member of this one serves the same lookups, including the quota aggregate.

Measured on PostgreSQL 18.3 with 15,021 packets seeded through the API (one user with 5,001, fifty users with 200 each), `EXPLAIN (ANALYZE, BUFFERS)` of the exact statements Marten emits, warm, after `ANALYZE`. The measurements were taken on the snapshot table of the event-sourced model; the document table has the same columns and the same index, so the plans are the same:

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

1. After a create, a delete or a preview update commits, `PacketStoreService` sends a PostgreSQL `NOTIFY` on the `passel_packet_events` channel. The payload carries the user id, packet id and the change (`Created`, `Deleted` or `Updated`).
2. Each API instance runs `PacketNotificationListener`, a hosted service that `LISTEN`s on that channel on its own connection. It reconnects after 5 seconds if the connection fails, and sends a keep-alive query when the connection is idle. Npgsql raises one notification per wait on the connection, so the connection loop only queues what it parses, and a dispatch loop takes everything queued at once as a batch: it loads the created and updated packets of users with a stream open on the instance with one query, publishes per user in arrival order (`packet-created` or `packet-updated`, each carrying the packet as it is now), and publishes deletes without a load. If the query fails, each affected user gets a `resync` instead.
3. The listener publishes to `PacketEventHub`, an in-memory hub on that instance. The hub fans events out to each user's open streams through bounded channels of 64 events.

Because every instance listens, events reach clients connected to any instance. The stream doesn't support resuming. Instead, clients reconnect, receive `resync`, and fetch `GET /api/packets` again. The server also sends `resync` when it may have missed events: after the listener reconnects, every open stream on the instance gets one, spread at random over three seconds so that the re-fetches don't all reach a database that may just have recovered at the same moment. If a stream's buffer fills, the server closes the stream rather than drop events.

A stream sends a `ping` after 20 seconds without events. The wait is a delay that completes rather than a timeout that throws, because every idle stream times out every 20 seconds. The first event of a stream carries an SSE `retry` value drawn per stream between 2 and 6 seconds, so clients that honour it (a browser `EventSource` does; the extension and the mobile app use their own jittered backoff) reconnect spread out after a restart. Event-stream requests are left out of request tracing in `ServiceDefaults`, next to the health endpoints, because a stream's request lasts up to a day and its trace would hold an activity open for all of it.

Streams are capped by the `EventStreams` configuration. The hub keeps at most five per user on an instance: subscribing a sixth removes the oldest from the hub and completes its channel, so that client's stream ends cleanly and it reconnects. Beyond the per-instance total, `Subscribe` returns `null` and the endpoint answers 503 with `Retry-After`. The subscription is registered for disposal with the response, so the hub releases it when the stream ends however it ends.

A stream never outlives the token that opened it: the endpoint reads the `exp` claim and cancels the stream at `exp` plus the bearer handler's clock skew (`PacketEndpoints.StreamLifetime`), with a ceiling of a day for principals without an expiry. The clients already reconnect with a fresh token.

## Link previews

When a link packet is created, its page is fetched in the background and what the page says about itself is stored on the link (`LinkPreview` on `PacketContent.Link`: title, description, image URL and when they were read). The fetch never runs inside the request: `PacketStoreService` queues it on `LinkPreviewQueue`, a bounded in-memory channel, once the create has committed, and `LinkPreviewWorker`, a hosted service, runs the queued fetches a few at a time. A fetch still queued when the instance stops is lost, and the link then keeps no preview, which is the same outcome as a page that gave none.

The API is fetching URLs that users chose, so `LinkPreviewFetcher` is held to rules that no configuration loosens:

- Only absolute `http` and `https` URLs without credentials are requested, on the URL the user sent and on every redirect. Nothing else is ever fetched: not the image a page names, not the links on it.
- The fetcher resolves each host itself and checks every address it gets with `LinkPreviewAddressPolicy`, which allows public unicast addresses only: no loopback, private (`10/8`, `172.16/12`, `192.168/16`), carrier-grade NAT (`100.64/10`), link-local (`169.254/16`, where the cloud metadata services live), multicast, reserved or documentation ranges, in IPv4 or IPv6. An IPv6 address that stands for an IPv4 one (IPv4-mapped, NAT64) is judged by that address; the deprecated IPv4-compatible and 6to4 forms are refused outright. If any address of a host is refused, the whole fetch is.
- The connection is made to one of the checked addresses, through the handler's connect callback, never to a fresh resolution of the name, so a name whose answer changes between the check and the connection (DNS rebinding) gets the checked address all the same. TLS still validates the certificate against the host name. No proxy is used, and no cookies.
- Redirects are followed by hand, at most three, each hop checked like the first URL.
- Only `text/html` and `application/xhtml+xml` responses are read, at most 512 KB of them and no further than the end of the `<head>`, within one five-second budget for the whole fetch, redirects included. No `Accept-Encoding` is sent, so the cap counts the bytes the page sends.

`LinkPreviewExtractor` reads the head, with comments, scripts and styles taken out, for the Open Graph tags first, the Twitter card tags next, and the plain `<title>` and `<meta name="description">` last. Entities are decoded, whitespace collapsed, the title cut to 256 characters and the description to 512, and a relative image URL made absolute against the page; an image that is not an `http` or `https` URL is dropped. A page with no title, description or image gives no preview.

A failure that may pass (a timeout, a connection or DNS failure, a 429 or 5xx answer) is retried: three attempts in all, five seconds before the second and ten before the third. A refusal, a 4xx answer, a page that is not HTML or one redirect too many is final. Either way, a failed fetch shows up only as the link keeping `preview: null`, plus one Information log line naming the packet and the reason; nothing is stored or announced about it.

When something was found, the worker attaches it through `PacketStoreService` with a Marten update rather than a store: a store is an upsert and would bring back a link deleted between the fetch and the save, whereas Marten refuses to update a document that is gone, and the preview is dropped with it. The commit is followed by a `NOTIFY` with the change `Updated`, which the listener loads like a create and the streams send as `packet-updated`, carrying the link in the `PacketResponse` shape with its `preview` set. The limits, the retry policy and the concurrency come from the `LinkPreviews` configuration section; the address rules do not. See [Configuration](../reference/configuration.md#web-api).

The integration tests fetch from a Kestrel server of their own on the loopback (`TestPageServer`), reached by the name `pages.test`, which the test factory's stand-in resolver points at the loopback while it resolves every other name to a private address. So the suite never reaches the internet, and the address rules stay in force for everything but that one name.

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

## Integration tests

`Invex.Passel.WebApi.IntegrationTests` runs the API as `Program.cs` builds it, in the Development environment, against a real PostgreSQL. The suite needs Docker running and nothing else:

```
dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj
```

`dotnet atom TestWebApi` runs it after the unit tests, and so do the `Validate` and `Build` workflows, since GitHub-hosted runners have Docker.

The suite starts one `postgres:18.3` container (the image the AppHost runs) with [Testcontainers](https://dotnet.testcontainers.org/) and hosts the API in-process with `WebApplicationFactory`, pointed at the container through the `passel-db` connection string. Requests are signed in with the Development `Authorization: Dev <user>` scheme, and every test acts as users of its own, so the tests share one database without seeing each other's packets. The rate limits are raised for the run, because paging alone creates 51 packets in a minute; the limits have unit tests of their own. Before the first test, the fixture creates packets until one arrives on an event stream, because the notification listener connects after the host has started and a packet created before then is announced to nobody.

The tests speak HTTP only, as the clients do: they assert the JSON and the events, never tables or Marten types, apart from the privilege checks of the migration test. They cover creating and listing with ownership isolation, paging over 51 packets through the cursor, the delete semantics (a second delete and another user's packet are both 404), the event stream end to end (a create and a delete committed through the API arrive as `packet-created` and `packet-deleted` on the owner's stream, through the database notification, and on nobody else's), link previews (the page of a new link is fetched from the tests' own server, its preview listed and announced as `packet-updated`, a page with nothing to show and a refused host leave the link without one, and a link deleted while its page is being fetched stays deleted), the schema: a second API host on an empty database serves a create as its very first request; the production path: after `db-migrate` (run twice) the API serves creates, lists, events and deletes on a role that only has DML, which the test then checks can neither create nor alter a table, and without the migration it does not start; and the contract files described next. A run takes under ten seconds once the images are local, about thirty the first time, and removes its container when it ends.

## Contracts

Packets have one wire shape, `PacketEndpoints.PacketResponse` (`Endpoints/PacketEndpoints/PacketResponse.cs`): a `GET /api/packets` item, the `POST /api/packets` response and the `packet-created` and `packet-updated` event data are all this type, built from the stored `Packet` document by `PacketResponse.From`, the only mapper. The request body (`PostPacketRequestBody`) stays a type of its own, because a packet has no id and no creation time before it is stored, and the `PacketContent` union in `Domain/Packet.cs` is the storage shape, not the wire. Each endpoint declares its responses, so the OpenAPI document carries the shapes and the problem responses.

The `contracts/` folder at the repository root holds what the API puts on the wire, and the integration tests (`ContractTests`) keep it true:

- `contracts/openapi.json` is the document the Development host serves at `/openapi/v1.json`, which is why it declares the Dev scheme next to the bearer token, minus the `servers` entry, which names whatever address served the copy. The test fails when the committed document differs from the served one.
- `contracts/fixtures/` holds a list page with every packet type (the link with its preview), a create response, a `packet-created`, a `packet-updated` and a `packet-deleted` event payload, serialized from fixed packets with the host's real `JsonOptions` (indented, with LF line endings, which the clients never see). One test fails when a committed fixture differs from what the API serializes now, another when an endpoint answers with properties other than its fixture's, and the extension and mobile app suites parse every fixture with their real parsers.
- `contracts/feed-scenarios.json` describes feed sequences and the list each must leave behind; both client suites run them through their feed stores, and the API tests check that every packet in the file is a valid wire packet.

To regenerate the document and the fixtures after a change, run the integration tests with `PASSEL_UPDATE_CONTRACTS=1` set, review the diff and commit it with the change; see [Contracts](../guides/testing.md#contracts) in the testing guide.
