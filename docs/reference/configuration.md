# Configuration

## AppHost

| Parameter | Secret | Passed to |
|---|---|---|
| `google-client-id` | No | The API's `Authentication:Google:ClientId` |
| `google-client-secret` | Yes | The API's `Authentication:Google:ClientSecret` |
| `google-extension-client-id` | No | The API's `Authentication:Google:AdditionalAudiences:0`, and the extension's `GOOGLE_CLIENT_ID` |

The values are stored in the AppHost's user secrets as `Parameters:<name>`. The dashboard prompts for any that are missing. The AppHost also gives the extension `API_URL` (`<API https endpoint>/api/packets`) and gives the API its `passel-db` connection string.

## Web API

| Key | Required | Description |
|---|---|---|
| `ConnectionStrings:passel-db` | Yes | PostgreSQL connection string. The AppHost provides it. |
| `Authentication:Google:ClientId` | Yes | The web client id: the main accepted token audience, and the client for development login |
| `Authentication:Google:ClientSecret` | Development | The web client secret, for the development-only Scalar login |
| `Authentication:Google:AdditionalAudiences` | No | Other accepted audiences, for example the extension's client id |
| `Encoding:Alphabet` | No | The Sqids alphabet for public ids. Changing it invalidates existing ids. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Turns on OTLP export of logs, traces and metrics. The AppHost points it at the dashboard. |
| `RateLimits:CreatesPerMinute` | No | Packets a user may create per minute. Default 30. |
| `RateLimits:ReadsPerMinute` | No | List and delete requests a user may make per minute. Default 120. |
| `RateLimits:StreamsPerMinute` | No | Event streams a user may open per minute. Default 10. |

Rate limits are sliding one-minute windows counted per signed-in user (the `sub` claim), or per remote address for requests that aren't authenticated. A request over the limit gets a 429 problem with a `Retry-After` header.

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
