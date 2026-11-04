# Configuration

## AppHost

| Parameter | Secret | Passed to |
|---|---|---|
| `google-client-id` | No | The API's `Authentication:Google:ClientId` |
| `google-client-secret` | Yes | The API's `Authentication:Google:ClientSecret` |
| `google-extension-client-id` | No | The API's `Authentication:Google:AdditionalAudiences:0`, and the extension's `GOOGLE_CLIENT_ID` |

The values are stored in the AppHost's user secrets as `Parameters:<name>`. In Development all three are optional and default to empty, so the dashboard doesn't prompt for them; the API then works through the Dev scheme described in [Run without Google](../getting-started/running-locally.md#run-without-google). Outside Development they're required. `google-client-secret` is only used locally: a published or deployed AppHost never asks for it, and the API in Azure never has it. The AppHost also gives the extension `API_URL` (`<API https endpoint>/api/packets`) and gives the API its `passel-db` connection string.

The AppHost reads one setting of its own:

| Key | Default | Description |
|---|---|---|
| `DevTunnel:AllowAnonymous` | `true` | Whether the `passel-api-tunnel` resource lets anyone with the URL through. Set it to `false` (in user secrets, the git-ignored `appsettings.Development.json`, or the `DevTunnel__AllowAnonymous` environment variable) and the tunnel requires a Dev Tunnels access token, which the mobile app sends from `TUNNEL_ACCESS_TOKEN`; see [Requiring a token](../getting-started/running-locally.md#requiring-a-token). The tunnel only runs when started from the dashboard either way. |

## Azure deployment

`aspire publish` and `aspire deploy` add the Azure resources to the AppHost ([Releasing and Deploying](../guides/deploying.md)). `DeployWebApi` supplies everything below from the Deploy workflow's variables and secret; nothing needs typing in.

| Setting | Secret | Source in the workflow | Description |
|---|---|---|---|
| `Azure:SubscriptionId`, `Azure:ResourceGroup`, `Azure:Location` | No | `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | Where Aspire deploys. The resource group must exist (`Azure:AllowResourceGroupCreation` is `false`). |
| `Azure:CredentialSource` | No | Always `AzureCli` | The Azure CLI session that `azure/login` leaves behind |
| `Parameters:google-client-id` | No | `GOOGLE_WEB_CLIENT_ID` | The API's `Authentication:Google:ClientId` |
| `Parameters:google-extension-client-id` | No | `CHROME_EXTENSION_CLIENT_ID` | The API's `Authentication:Google:AdditionalAudiences:0` |
| `Parameters:encoding-alphabet` | Yes | `ENCODING_ALPHABET` (environment secret) | Stored in Key Vault as `encoding-alphabet`; the container app reads it as the API's `Encoding:Alphabet`. Must never change once ids are in use. |
| `Deployment:ImageTag` | No | GitVersion's version for the commit | The tag the image is pushed with. Without it, it is pushed as `latest`. |
| `Deployment:ApiDomain` | No | `API_DOMAIN` | The API's custom domain. Empty for none; when set, the container app binds it and `AllowedHosts` includes it. |
| `Parameters:api-certificate-name` | No | `API_CERTIFICATE_NAME` | The managed certificate for the domain; empty for the first deployment with it. Only asked for when `Deployment:ApiDomain` is set. |

In Azure the API's container gets:

| Variable | Value |
|---|---|
| `ConnectionStrings__passel-db` | `Host=<server>;Database=passel-db;Username=<the API identity's name>;Ssl Mode=Require`, with no password |
| `Encoding__Alphabet` | The Key Vault secret, through the API identity |
| `AllowedHosts` | The container app's default host name, `localhost` (the health probes send it) and the custom domain when there is one |
| `Authentication__Google__ClientId`, `Authentication__Google__AdditionalAudiences__0` | From the parameters above |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | From the Application Insights resource |
| `AZURE_CLIENT_ID`, `AZURE_TOKEN_CREDENTIALS` | Set by Aspire, so the Entra tokens are for the API's managed identity |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `HTTP_PORTS` | Set by Aspire for Container Apps ingress |

## Web API

| Key | Required | Description |
|---|---|---|
| `ConnectionStrings:passel-db` | Yes | PostgreSQL connection string. The AppHost provides it. Without a password (as in Azure) the API signs in with a Microsoft Entra token for its managed identity, and the user name, if missing, is taken from the token. |
| `Authentication:Google:ClientId` | Outside Development | The web client id: the main accepted token audience, and the client for development login. Without it every Google token is rejected; in Development the Dev scheme still works. |
| `Authentication:Google:ClientSecret` | No | The web client secret. With it and the client id both set, Development offers the Scalar **Login with Google** banner. |
| `Authentication:Google:AdditionalAudiences` | No | Other accepted audiences, for example the extension's client id |
| `Encoding:Alphabet` | No | The Sqids alphabet for public ids. Changing it invalidates existing ids. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Turns on OTLP export of logs, traces and metrics. The AppHost points it at the dashboard. Ignored when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | Sends logs, traces and metrics to Application Insights instead, with the Azure Monitor OpenTelemetry distro. The Azure deployment sets it. |
| `RateLimits:CreatesPerMinute` | No | Packets a user may create per minute. Default 30. |
| `RateLimits:ReadsPerMinute` | No | List and delete requests a user may make per minute. Default 120. |
| `RateLimits:StreamsPerMinute` | No | Event streams a user may open per minute. Default 10. |
| `Quotas:MaxPacketsPerUser` | No | The most packets a user may have at once. Default 5000. |
| `Quotas:MaxBytesPerUser` | No | The most bytes a user may have stored across their packets. Default 52428800 (50 MB). |
| `EventStreams:MaxStreamsPerUser` | No | Event streams one user may hold open on one instance; opening another closes their oldest. Default 5. |
| `EventStreams:MaxStreamsPerInstance` | No | Event streams one instance holds open in total; beyond it the endpoint answers 503. Default 4000. |
| `AllowedHosts` | No | The hosts the API answers to, separated by `;`. `appsettings.json` sets the production host, `api.passel.link`; a deployment on another host overrides it, and must include the host its health probes use. Ignored in Development, which answers to any host. |
| `ForwardedHeaders:Enabled` | No | Behind a reverse proxy, take the client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. Default `false`. Leave it off in Azure Container Apps: the deployment sets ASP.NET Core's own `ASPNETCORE_FORWARDEDHEADERS_ENABLED` there, and turning both on would apply the headers twice, letting a client choose the address the rate limits see. |
| `ForwardedHeaders:KnownProxies` | No | The proxy addresses to accept those headers from, as a list. |
| `ForwardedHeaders:KnownNetworks` | No | The proxy networks to accept those headers from, in CIDR notation, as a list. With neither list set, the headers are accepted from whatever connects directly, which is only safe when the proxy is the one way to reach the API. |
| `Kestrel:Limits:MaxConcurrentConnections` | No | The most open connections, event streams included. Default 5000. The rest of the `Kestrel` section binds too. |
| `LinkPreviews:Enabled` | No | Whether the page of a new link is fetched for a preview. Default `true`. |
| `LinkPreviews:Timeout` | No | How long one fetch attempt may take in all, redirects and body included. Default `00:00:05`. |
| `LinkPreviews:MaxBodyBytes` | No | The most bytes read from a page. Default 524288 (512 KB). |
| `LinkPreviews:MaxRedirects` | No | How many redirects a fetch follows, each checked like the first URL. Default 3. |
| `LinkPreviews:MaxAttempts` | No | How often a fetch is attempted before the link is left without a preview; only timeouts, connection failures and 429 or 5xx answers are retried. Default 3. |
| `LinkPreviews:RetryDelay` | No | The wait before the second attempt; each later attempt waits that much longer again. Default `00:00:05`. |
| `LinkPreviews:MaxConcurrentFetches` | No | How many pages one instance fetches at the same time. Default 4. |
| `LinkPreviews:QueueCapacity` | No | How many links may wait for a fetch on one instance; beyond it, new links go without a preview. Default 1000. |

Outside Development, `appsettings.Production.json` keeps Npgsql's log of every command it runs to warnings, so production logs don't carry each SQL statement.

Link previews are fetched after a link is created, behind guards that no setting loosens: `http` and `https` only, public addresses only, checked on every redirect. See [Link previews](../components/web-api.md#link-previews).

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
| `DEV_SIGN_IN` | `true` lets a development build (`npm run dev`, `npm run dev:watch`) sign in as a development user through the API's Development-only `Dev` scheme, without Google; `GOOGLE_CLIENT_ID` may then be left empty. A minified build (`npm run build`) never has it. The AppHost sets it. |

## Mobile app

`src/mobile_app/.env` (template: `.env.example`) is compiled in with `--dart-define-from-file`.

| Key | Required | Description |
|---|---|---|
| `API_BASE_URL` | Yes | The API root as the device can reach it: `http://localhost:5218` for a phone on USB (with `adb reverse tcp:5218 tcp:5218`), `http://10.0.2.2:5218` for an emulator, or the dev tunnel URL. The production flavor requires `https://`; the development flavor allows `http://` only to `localhost`, `127.0.0.1` and `10.0.2.2`. |
| `SERVER_CLIENT_ID` | Unless `DEV_USER` is set | The Google web client id |
| `APP_CLIENT_ID` | No | The Android client id |
| `TUNNEL_ACCESS_TOKEN` | No | A Dev Tunnels access token (`devtunnel token <tunnel-id> --scopes connect`), sent as `X-Tunnel-Authorization: tunnel <token>` with every request, for a tunnel that doesn't allow anonymous access. Development flavor only: the production flavor refuses to start with one. |
| `DEV_USER` | No | Signs the development flavor in as this user through the API's Development-only `Dev` scheme (`Authorization: Dev <user>`) instead of Google, so no Google client is needed. The login page can sign in as any other user, and each user is a separate account. Development flavor only: the production flavor refuses to start with one. |

## Build

`_atom/appsettings.json` holds build parameter defaults; see [Build Targets](build-targets.md). The deployment's parameters are listed with [`DeployWebApi`](build-targets.md#deployment).
