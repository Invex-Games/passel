# Configuration

## AppHost

| Parameter | Secret | Passed to |
|---|---|---|
| `google-client-id` | No | The API's `Authentication:Google:ClientId`, and the web app's `GOOGLE_CLIENT_ID` |
| `google-client-secret` | Yes | The API's `Authentication:Google:ClientSecret` |
| `google-extension-client-id` | No | The API's `Authentication:Google:AdditionalAudiences:0`, and the extension's `GOOGLE_CLIENT_ID` |

The values are stored in the AppHost's user secrets as `Parameters:<name>`. In Development all three are optional and default to empty, so the dashboard doesn't prompt for them; the API then works through the Dev scheme described in [Run without Google](../getting-started/running-locally.md#run-without-google). Outside Development they're required. `google-client-secret` is only used locally: a published or deployed AppHost never asks for it, and the API in Azure never has it. The AppHost also gives the extension and the web app `API_URL` (`<API https endpoint>/api/packets`), runs the web app on `PORT` 5245, and gives the API its `passel-db` connection string, the `passel-blobs` connection string of the `passel-storage` resource (Azurite, in a persistent container with a data volume, so stored files survive AppHost restarts as the database does) and the web app's origin, `http://localhost:5245`, as `Cors:AllowedOrigins:0`. The API waits for the `parcels` container to exist before it starts.

The AppHost reads one setting of its own:

| Key | Default | Description |
|---|---|---|
| `DevTunnel:AllowAnonymous` | `true` | Whether the `passel-api-tunnel` resource lets anyone with the URL through. Set it to `false` (in user secrets, the git-ignored `appsettings.Development.json`, or the `DevTunnel__AllowAnonymous` environment variable) and the tunnel requires a Dev Tunnels access token, which the mobile app sends from `TUNNEL_ACCESS_TOKEN`; see [Requiring a token](../getting-started/running-locally.md#requiring-a-token). The tunnel only runs when started from the dashboard either way. |

## Azure deployment

`aspire publish` and `aspire deploy` add the Azure resources to the AppHost ([Releasing and Deploying](../guides/deploying.md)). `DeployWebApi` supplies everything below from the Deploy workflow's variables and secret; nothing needs typing in.

| Setting | Secret | Source in the workflow | Description |
|---|---|---|---|
| `Azure:SubscriptionId`, `Azure:ResourceGroup`, `Azure:Location` | No | `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | Where Aspire deploys. The resource group must exist (`Azure:AllowResourceGroupCreation` is `false`), and the migration step needs the first two to find the job. |
| `Azure:CredentialSource` | No | Always `AzureCli` | The Azure CLI session that `azure/login` leaves behind |
| `Parameters:google-client-id` | No | `GOOGLE_WEB_CLIENT_ID` | The API's `Authentication:Google:ClientId` |
| `Parameters:google-extension-client-id` | No | `CHROME_EXTENSION_CLIENT_ID` | The API's `Authentication:Google:AdditionalAudiences:0` |
| `Parameters:encoding-alphabet` | Yes | `ENCODING_ALPHABET` (environment secret) | Stored in Key Vault as `encoding-alphabet`; the container app reads it as the API's `Encoding:Alphabet`. Must never change once ids are in use. |
| `Deployment:ImageTag` | No | GitVersion's version for the commit | The tag both images are pushed with. Without it they are pushed as `latest`. |
| `Deployment:ApiDomain` | No | `API_DOMAIN` | The API's custom domain. Empty for none; when set, the container app binds it and `AllowedHosts` includes it. |
| `Parameters:api-certificate-name` | No | `API_CERTIFICATE_NAME` | The managed certificate for the domain; empty for the first deployment with it. Only asked for when `Deployment:ApiDomain` is set. |

In Azure the API's container gets:

| Variable | Value |
|---|---|
| `ConnectionStrings__passel-db` | `Host=<server>;Database=passel-db;Username=<the API identity's role>;Ssl Mode=Require`, with no password |
| `ConnectionStrings__passel-blobs` | The storage account's blob endpoint (`https://<account>.blob.core.windows.net/`), with no key: the API signs in as its identity, which has Storage Blob Data Contributor on the account and no other storage role. Aspire also sets the same URI as `ConnectionStrings__passel_blobs` and `PASSEL_BLOBS_URI`, which the API doesn't read. |
| `Encoding__Alphabet` | The Key Vault secret, through the API identity |
| `AllowedHosts` | The container app's default host name, `localhost` (the health probes send it) and the custom domain when there is one |
| `Authentication__Google__ClientId`, `Authentication__Google__AdditionalAudiences__0` | From the parameters above |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | From the Application Insights resource |
| `AZURE_CLIENT_ID`, `AZURE_TOKEN_CREDENTIALS` | Set by Aspire, so the Entra tokens are for the API's managed identity |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `HTTP_PORTS` | Set by Aspire for Container Apps ingress |

The migration job gets the database's connection string (as its own identity, an Entra administrator) and `Database__ApiRole` and `Database__ApiRoleObjectId`, the API identity's role name and object id. It gets no storage connection string and no role on the storage account: it never touches files.

## Web API

| Key | Required | Description |
|---|---|---|
| `ConnectionStrings:passel-db` | Yes | PostgreSQL connection string. The AppHost provides it. Without a password (as in Azure) the API signs in with a Microsoft Entra token for its managed identity, and the user name, if missing, is taken from the token. |
| `ConnectionStrings:passel-blobs` | For files | Azure Blob Storage, where uploaded files are kept. The AppHost provides it (Azurite locally). A connection string with an account key, or Azurite's `UseDevelopmentStorage=true`, is used as it is; a plain blob endpoint URI (as in Azure) signs in with the managed identity. Without it the API still starts, every file endpoint answers 503, and outside Development it logs a warning at startup. |
| `Authentication:Google:ClientId` | Outside Development | The web client id: the main accepted token audience, and the client for development login. Without it every Google token is rejected; in Development the Dev scheme still works. |
| `Authentication:Google:ClientSecret` | No | The web client secret. With it and the client id both set, Development offers the Scalar **Login with Google** banner. |
| `Authentication:Google:AdditionalAudiences` | No | Other accepted audiences, for example the extension's client id |
| `Encoding:Alphabet` | No | The Sqids alphabet for public ids. Changing it invalidates existing ids. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Turns on OTLP export of logs, traces and metrics. The AppHost points it at the dashboard. Ignored when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | Sends logs, traces and metrics to Application Insights instead, with the Azure Monitor OpenTelemetry distro. The Azure deployment sets it. |
| `RateLimits:CreatesPerMinute` | No | Packets a user may create per minute. Default 30. |
| `RateLimits:ReadsPerMinute` | No | Packet list and delete requests, and device lists, a user may make per minute. Default 120. |
| `RateLimits:StreamsPerMinute` | No | Event streams a user may open per minute. Default 10. |
| `RateLimits:DeviceWritesPerMinute` | No | Device registrations, renames, removals and push token changes a user may make per minute. Default 20. |
| `RateLimits:ParcelUploadsPerMinute` | No | File uploads and discards a user may make per minute (`parcel-uploads`). Default 10. |
| `RateLimits:ParcelReadsPerMinute` | No | File downloads and thumbnails a user may fetch per minute (`parcel-reads`). Default 600, since a feed page can show fifty thumbnails. |
| `Quotas:MaxPacketsPerUser` | No | The most packets a user may have at once. Default 5000. |
| `Quotas:MaxBytesPerUser` | No | The most bytes a user may have stored across their packet documents. Default 52428800 (50 MB). Uploaded files don't count here; see `Quotas:MaxParcelBytesPerUser`. |
| `Quotas:MaxParcelBytesPerUser` | No | The most bytes of uploaded files a user may have at once: every file not deleted, sent or not, with an upload in progress counted at the size it declared. Default 524288000 (500 MB). |
| `Quotas:MaxPendingParcelsPerUser` | No | The most files a user may have uploaded but not sent, from 1 to 1000; they expire after `Parcels:UnattachedLifetime`, so this bounds what an abandoned client leaves behind. Default 20. |
| `Quotas:MaxDevicesPerUser` | No | The most devices a user may have registered at once, from 1 to 1000; the device list is not paged, so this also bounds its length. Default 20. |
| `Parcels:ContainerName` | No | The blob container files are kept in. The API creates it if it's missing. Default `parcels`. |
| `Parcels:MaxParcelBytes` | No | The largest file one upload may carry, at most 1 GiB. Default 26214400 (25 MB). Above about 30 MB (Kestrel's default body limit), raise `Kestrel:Limits:MaxRequestBodySize` with it. |
| `Parcels:UnattachedLifetime` | No | How long an uploaded file waits to be sent before it is deleted, from a second to seven days. Default `01:00:00`. |
| `Parcels:UploadingGrace` | No | How long an upload may stay unfinished (its instance stopped part way) before it is taken to have failed and deleted, from a second to a day, and shorter than `Parcels:UnattachedLifetime`. Default `00:15:00`. |
| `Parcels:SweepEnabled` | No | Whether the sweep runs. Off, deleted and expired files keep their bytes until it is on again. Default `true`. |
| `Parcels:SweepInterval` | No | How often the sweep runs when nothing wakes it sooner (a delete wakes it at once), from a second to an hour. Default `00:05:00`. |
| `Parcels:OrphanScanInterval` | No | How often the sweep also looks for stored bytes with no file and files whose packet is gone, from a minute to seven days. Default `1.00:00:00`. |
| `Parcels:OrphanBlobMinimumAge` | No | How old stored bytes with no file must be before the orphan scan deletes them, up to seven days. Default `02:00:00`. |
| `Parcels:SweepBatchSize` | No | How many files the sweep expires or erases per batch, from 1 to 1000. Default 100. |
| `Parcels:ThumbnailSize` | No | The longest side of a thumbnail in pixels, from 64 to 2048. Default 512. |
| `Parcels:MaxImagePixels` | No | The most pixels an image may have to be decoded for a thumbnail; larger ones are kept as plain files. A PNG or GIF decodes at full size, four bytes a pixel, so this bounds the memory a decode takes. Default 40000000. |
| `Parcels:MaxImageDimension` | No | The longest side an image may have to be decoded for a thumbnail. Default 16384. |
| `Parcels:MaxConcurrentUploads` | No | How many uploads one instance takes at once, from 1 to 64; the next waits five seconds and then gets 503. Default 4. |
| `EventStreams:MaxStreamsPerUser` | No | Event streams one user may hold open on one instance; opening another closes their oldest. Default 5. |
| `EventStreams:MaxStreamsPerInstance` | No | Event streams one instance holds open in total; beyond it the endpoint answers 503. Default 4000. |
| `AllowedHosts` | No | The hosts the API answers to, separated by `;`. `appsettings.json` sets the production host, `api.passel.link`; a deployment on another host overrides it, and must include the host its health probes use. Ignored in Development, which answers to any host. |
| `ForwardedHeaders:Enabled` | No | Behind a reverse proxy, take the client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. Default `false`. Leave it off in Azure Container Apps: the deployment sets ASP.NET Core's own `ASPNETCORE_FORWARDEDHEADERS_ENABLED` there, and turning both on would apply the headers twice, letting a client choose the address the rate limits see. |
| `ForwardedHeaders:KnownProxies` | No | The proxy addresses to accept those headers from, as a list. |
| `ForwardedHeaders:KnownNetworks` | No | The proxy networks to accept those headers from, in CIDR notation, as a list. With neither list set, the headers are accepted from whatever connects directly, which is only safe when the proxy is the one way to reach the API. |
| `Cors:AllowedOrigins` | No | The browser origins allowed to call the API across origins, as a list: the web app's, such as `https://passel.link`. Each is written as the browser sends it in the `Origin` header (scheme, host, and a port only when it isn't the default; no path, no trailing slash, no wildcard), and the API refuses to start with one that isn't. Empty by default, which allows no origin. The AppHost sets the web app's local origin. |
| `Cors:PreflightMaxAge` | No | How long a browser may cache the answer to a preflight request. Default `00:10:00`, at most two hours. |
| `Kestrel:Limits:MaxConcurrentConnections` | No | The most open connections, event streams included. Default 5000. The rest of the `Kestrel` section binds too. |
| `LinkPreviews:Enabled` | No | Whether the page of a new link is fetched for a preview. Default `true`. |
| `LinkPreviews:Timeout` | No | How long one fetch attempt may take in all, redirects and body included. Default `00:00:05`. |
| `LinkPreviews:MaxBodyBytes` | No | The most bytes read from a page. Default 524288 (512 KB). |
| `LinkPreviews:MaxRedirects` | No | How many redirects a fetch follows, each checked like the first URL. Default 3. |
| `LinkPreviews:MaxAttempts` | No | How often a fetch is attempted before the link is left without a preview; only timeouts, connection failures and 429 or 5xx answers are retried. Default 3. |
| `LinkPreviews:RetryDelay` | No | The wait before the second attempt; each later attempt waits that much longer again. Default `00:00:05`. |
| `LinkPreviews:MaxConcurrentFetches` | No | How many pages one instance fetches at the same time. Default 4. |
| `LinkPreviews:QueueCapacity` | No | How many links may wait for a fetch on one instance; beyond it, new links go without a preview. Default 1000. |
| `Database:ApiRole` | No | Only read by `db-migrate`: the role the API logs in as, which it grants the API's DML-only privileges. See [Schema in production](../components/web-api.md#schema-in-production). |
| `Database:ApiRoleObjectId` | No | Only read by `db-migrate`: the Microsoft Entra object id of the API's managed identity, so the role is created as its Entra principal. |

Outside Development, `appsettings.Production.json` keeps Npgsql's log of every command it runs to warnings, so production logs don't carry each SQL statement.

Link previews are fetched after a link is created, behind guards that no setting loosens: `http` and `https` only, public addresses only, checked on every redirect. See [Link previews](../components/web-api.md#link-previews).

CORS is for the web app alone: the extension's host permissions bypass CORS, and the mobile app isn't a browser. An origin in `Cors:AllowedOrigins` may call the API with `GET`, `POST` and `DELETE`, sending `Authorization`, `Content-Type` and `Accept`, and `Range` and `If-None-Match` to resume or revalidate a file download, and may read `Retry-After`, `Location`, `ETag`, `Content-Disposition`, `Content-Range`, `Accept-Ranges` and `Content-Length`; credentials (cookies) are never allowed, since the clients send a bearer token. Preflight requests are answered ahead of authentication and rate limiting. Any other origin gets no CORS headers, so the browser keeps the answer from its page. Outside Development the API also sends HSTS.

Rate limits are sliding one-minute windows counted per signed-in user (the `sub` claim), or per remote address for requests that aren't authenticated. A request over the limit gets a 429 problem with a `Retry-After` header.

Quotas are checked before every create with one indexed aggregate over the user's packets. The byte quota counts the stored size of the packet documents, which PostgreSQL compresses when they're large, so it's a storage cap rather than an exact character count. A user at the packet limit gets a 429 problem; a packet that would pass the byte limit gets a 413 problem. Both say to delete packets. The device cap is checked only when an installation registers for the first time, so a device that is registered already is never refused; a user at the cap gets a 429 problem saying to remove a device. New installations registering at the same moment are all checked against the same count, so a burst can pass the cap by up to `RateLimits:DeviceWritesPerMinute` less one; raising that limit loosens the cap too. Lowering the cap removes nobody's devices.

Uploads are checked against their own quotas, before the body is read and again once the file is stored: a file over `Parcels:MaxParcelBytes` or one that would pass `Quotas:MaxParcelBytesPerUser` gets a 413 problem, and an upload beyond `Quotas:MaxPendingParcelsPerUser` unsent files a 429 problem. Lowering a limit deletes no files. The upload endpoint sets the request body limit of its own requests to `Parcels:MaxParcelBytes` plus 64 KB for the multipart framing, and counts the file itself as it reads it. When the file limit is set above Kestrel's default of about 30 MB, raise `Kestrel:Limits:MaxRequestBodySize` with it; only the endpoint's own count is covered by the tests, because the test server enforces no body limit.

When you run the API outside the AppHost, put these in `src/webapi/Invex.Passel.WebApi/appsettings.Development.json`. The file is git-ignored by the `*.Development.json` rule. Settings supplied by the AppHost take precedence over the file.

## Chrome extension

`src/chrome-extension/.env` (template: `.env.example`) is only read by standalone builds; the AppHost supplies these values itself.

| Key | Description |
|---|---|
| `API_URL` | The packets endpoint, for example `https://localhost:7030/api/packets` |
| `GOOGLE_CLIENT_ID` | The extension's OAuth client id |
| `DEV_SIGN_IN` | `true` lets a development build (`npm run dev`, `npm run dev:watch`) sign in as a development user through the API's Development-only `Dev` scheme, without Google; `GOOGLE_CLIENT_ID` may then be left empty. A minified build (`npm run build`) never has it. The AppHost sets it. |

## Web app

`src/web/.env` (template: `.env.example`) is only read by standalone builds; the AppHost supplies these values itself. See [Web App](../components/web-app.md#build).

| Key | Description |
|---|---|
| `API_URL` | The packets endpoint, for example `https://localhost:7030/api/packets`. Its origin goes into the app's Content-Security-Policy. |
| `GOOGLE_CLIENT_ID` | The Google OAuth **web** client id, whose Authorized JavaScript origins must include the app's origin |
| `DEV_SIGN_IN` | `true` lets a development build (`npm run dev`) sign in as a development user through the API's Development-only `Dev` scheme, without Google; `GOOGLE_CLIENT_ID` may then be left empty. A minified build (`npm run build`) never has it. The AppHost sets it. |
| `PORT` | The dev server's port (`npm run dev`, `npm run serve`). Default 5245. The AppHost sets it. |

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
