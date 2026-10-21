# Web API

`src/webapi` holds four projects:

| Project | Purpose |
|---|---|
| `Invex.Passel.WebApi` | The API |
| `Invex.Passel.WebApi.Tests` | Unit tests (xUnit v3 on Microsoft.Testing.Platform) |
| `Invex.Passel.AppHost` | The Aspire AppHost that runs the local environment |
| `Invex.Passel.ServiceDefaults` | OpenTelemetry, health checks, service discovery and HTTP resilience, shared Aspire-style |

## Request pipeline

`Program.cs` adds the service defaults and the web defaults (`Extensions/HostWebExtensions.cs`). Those cover JSON options, ProblemDetails, CORS, compression, authentication and API versioning. It then maps the packet endpoints (`Endpoints/EndpointMapper.cs`). In development it also maps OpenAPI, Scalar and the browser login endpoints (`Extensions/HostDevExtensions.cs`).

Every packet endpoint lives under `/api/packets`, requires authentication and is API version 1. The user id is the Google `sub` claim. See [HTTP API](../reference/http-api.md) for the endpoints themselves.

## Storage: Marten event sourcing

Packets are stored with [Marten](https://martendb.io/) on PostgreSQL.

- Each packet is an event stream keyed by a version 7 `Guid`. The events are `TextPacketCreated`, `LinkPacketCreated`, `FilePacketCreated` and `PacketDeleted`.
- `PacketAggregate` is kept as an **inline snapshot**: a document written in the same transaction as the event. Reads query the snapshot documents. `PacketDeleted` removes the document.
- `PacketStoreService` handles creating and deleting packets. Deleting checks ownership first. A missing packet and someone else's packet both return 404, so ids can't be probed.
- Snapshot documents are indexed on `UserId`.

File packets exist in the model, but nothing creates them yet: the API rejects them with 400 and there is no file storage.

## Public ids

Packet ids are never exposed as raw Guids. `IdEncodingService` encodes the Guid's two 64-bit halves with [Sqids](https://sqids.org/) (minimum length 10). The alphabet comes from `Encoding:Alphabet`. Changing it invalidates every id that clients have stored.

## Live updates

`GET /api/packets/events` streams server-sent events to signed-in clients:

1. After a create or delete commits, `PacketStoreService` sends a PostgreSQL `NOTIFY` on the `passel_packet_events` channel. The payload carries the user id, packet id and the change.
2. Each API instance runs `PacketNotificationListener`, a hosted service that `LISTEN`s on that channel on its own connection. It reconnects after 5 seconds if the connection fails, and sends a keep-alive query when the connection is idle.
3. The listener publishes to `PacketEventHub`, an in-memory hub on that instance. The hub fans events out to each user's open streams through bounded channels of 64 events.

Because every instance listens, events reach clients connected to any instance. The stream doesn't support resuming. Instead, clients reconnect, receive `resync`, and fetch `GET /api/packets` again. The server also sends `resync` when it may have missed events. If a stream's buffer fills, the server closes the stream rather than drop events.

## Authentication

Clients send a Google **ID token** as a bearer token. The API validates it against Google with these accepted audiences:

- `Authentication:Google:ClientId`
- `Authentication:Google:AdditionalAudiences`

In development a policy scheme picks how each request is authenticated:

- A request with an `Authorization` header uses bearer tokens.
- Any other request uses a cookie, issued by Google OpenID Connect login.

That cookie login is what lets Scalar call authenticated endpoints. Production only accepts bearer tokens, and unauthenticated requests get a plain 401.

See [Google OAuth Clients](../getting-started/google-oauth.md) for which client is which.
