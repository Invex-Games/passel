# Configuration

## AppHost

| Parameter | Secret | Passed to |
|---|---|---|
| `google-client-id` | No | The API's `Authentication:Google:ClientId` |
| `google-client-secret` | Yes | The API's `Authentication:Google:ClientSecret` |
| `google-extension-client-id` | No | The API's `Authentication:Google:AdditionalAudiences:0`, and the extension's `GOOGLE_CLIENT_ID` |

The values are stored in the AppHost's user secrets as `Parameters:<name>`. In Development all three are optional and default to empty, so the dashboard doesn't prompt for them; the API then works through the Dev scheme described in [Run without Google](../getting-started/running-locally.md#run-without-google). Outside Development they're required. The AppHost also gives the extension `API_URL` (`<API https endpoint>/api/packets`) and gives the API its `passel-db` connection string.

## Web API

| Key | Required | Description |
|---|---|---|
| `ConnectionStrings:passel-db` | Yes | PostgreSQL connection string. The AppHost provides it. |
| `Authentication:Google:ClientId` | Outside Development | The web client id: the main accepted token audience, and the client for development login. Without it every Google token is rejected; in Development the Dev scheme still works. |
| `Authentication:Google:ClientSecret` | No | The web client secret. With it and the client id both set, Development offers the Scalar **Login with Google** banner. |
| `Authentication:Google:AdditionalAudiences` | No | Other accepted audiences, for example the extension's client id |
| `Encoding:Alphabet` | No | The Sqids alphabet for public ids. Changing it invalidates existing ids. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Turns on OTLP export of logs, traces and metrics. The AppHost points it at the dashboard. |
| `RateLimits:CreatesPerMinute` | No | Packets a user may create per minute. Default 30. |
| `RateLimits:ReadsPerMinute` | No | List and delete requests a user may make per minute. Default 120. |
| `RateLimits:StreamsPerMinute` | No | Event streams a user may open per minute. Default 10. |
| `Quotas:MaxPacketsPerUser` | No | The most packets a user may have at once. Default 5000. |
| `Quotas:MaxBytesPerUser` | No | The most bytes a user may have stored across their packets. Default 52428800 (50 MB). |
| `EventStreams:MaxStreamsPerUser` | No | Event streams one user may hold open on one instance; opening another closes their oldest. Default 5. |
| `EventStreams:MaxStreamsPerInstance` | No | Event streams one instance holds open in total; beyond it the endpoint answers 503. Default 4000. |
| `AllowedHosts` | No | The hosts the API answers to, separated by `;`. `appsettings.json` sets the production host, `api.passel.link`; a deployment on another host overrides it, and must include the host its health probes use. Ignored in Development, which answers to any host. |
| `ForwardedHeaders:Enabled` | No | Behind a reverse proxy, take the client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. Default `false`. |
| `ForwardedHeaders:KnownProxies` | No | The proxy addresses to accept those headers from, as a list. |
| `ForwardedHeaders:KnownNetworks` | No | The proxy networks to accept those headers from, in CIDR notation, as a list. With neither list set, the headers are accepted from whatever connects directly, which is only safe when the proxy is the one way to reach the API. |
| `Kestrel:Limits:MaxConcurrentConnections` | No | The most open connections, event streams included. Default 5000. The rest of the `Kestrel` section binds too. |

The API sends no CORS headers. Neither client needs them: the extension's host permissions bypass CORS, and the mobile app isn't a browser. The web app will add an allowlist of its own origin when it arrives. Outside Development the API also sends HSTS.

Rate limits are sliding one-minute windows counted per signed-in user (the `sub` claim), or per remote address for requests that aren't authenticated. A request over the limit gets a 429 problem with a `Retry-After` header.

Quotas are checked before every create with one indexed aggregate over the user's packets. The byte quota counts the stored size of the packet documents, which PostgreSQL compresses when they're large, so it's a storage cap rather than an exact character count. A user at the packet limit gets a 429 problem; a packet that would pass the byte limit gets a 413 problem. Both say to delete packets.

When you run the API outside the AppHost, put these in `src/webapi/Invex.Passel.WebApi/appsettings.Development.json`. The file is git-ignored by the `*.Development.json` rule. Settings supplied by the AppHost take precedence over the file.

## Chrome extension

`src/chrome-extension/.env` (template: `.env.example`) is only read by standalone builds; the AppHost supplies these values itself.

| Key | Description |
|---|---|
| `API_URL` | The packets endpoint, for example `https://localhost:7030/api/packets` |
| `GOOGLE_CLIENT_ID` | The extension's OAuth client id |

## Mobile app

`src/mobile_app/.env` (template: `.env.example`) is compiled in with `--dart-define-from-file`.

| Key | Required | Description |
|---|---|---|
| `API_BASE_URL` | Yes | The API root as the device can reach it, for example the dev tunnel URL |
| `SERVER_CLIENT_ID` | Yes | The Google web client id |
| `APP_CLIENT_ID` | No | The Android client id |

## Build

`_atom/appsettings.json` holds build parameter defaults; see [Build Targets](build-targets.md).
