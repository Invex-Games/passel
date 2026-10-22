# Running Locally

## Start the AppHost

```
aspire run
```

Or, without the Aspire CLI:

```
dotnet run --project src/webapi/Invex.Passel.AppHost
```

The Aspire dashboard opens at `https://localhost:17106`. It asks for a login token, because it shows the Google client secret and the database password: the console prints a link that carries the token (`https://localhost:17106/login?t=<token>`), and `aspire run` opens that link for you. The AppHost starts these resources:

| Resource | What it is |
|---|---|
| `passel-pg` | PostgreSQL in Docker, with pgweb for browsing data. The container is persistent, so data survives AppHost restarts. |
| `invex-passel-webapi` | The API at `https://localhost:7030`. It waits for the database. |
| `chrome-extension` | The extension's watch build. It rebuilds `src/chrome-extension/dist` on every change. |
| `passel-api-tunnel` | A dev tunnel giving the API a public HTTPS URL, for a phone that is not on USB. It doesn't start with the AppHost: start it from the dashboard when you need it, see [the dev tunnel](#a-phone-that-is-not-on-usb-the-dev-tunnel). |

The Google client values are optional in Development. Without them, use the Dev scheme described in [Run without Google](#run-without-google); to sign in with Google, set them in the AppHost's user secrets, see [Google OAuth Clients](google-oauth.md).

## Web API

- Scalar API reference: `https://localhost:7030/scalar`. Use the **Login with Google** banner, or the **Dev** security scheme, to call authenticated endpoints from the browser.
- Health checks: `/health` (every check, including the database) and `/alive` (liveness only).
- Logs, traces and metrics: the dashboard.

> [!WARNING]
> In development, Marten recreates any table whose schema doesn't match the code (`AutoCreate.All`). Expect local data to be dropped when you change the document model.

To run the API on its own, outside the AppHost, use its `https` launch profile. You then need to supply a `passel-db` connection string, and any `Authentication:Google:*` settings you want, in `src/webapi/Invex.Passel.WebApi/appsettings.Development.json` (git-ignored). See [Configuration](../reference/configuration.md#web-api).

## Run without Google

In Development the three Google values are optional, so a fresh clone runs without a Google Cloud project. Leave them unset and the API accepts a development scheme in place of Google tokens: `Authorization: Dev <user-id>` signs the request in as that user. An id is one to 255 letters, digits or `-._@+`, and different ids are different users.

```
curl -H "Authorization: Dev alice" https://localhost:7030/api/packets
```

In Scalar, open **Auth** (or the lock on a request), pick the **Dev** scheme and enter `Dev alice` as the value; Scalar then sends the header with every request. The banner at the top says when Google login isn't configured.

The scheme only exists while the API runs as `Development`; outside it, only Google tokens are accepted. The extension and the mobile app still sign in with Google, so they need the real clients from [Google OAuth Clients](google-oauth.md). Setting those parameters later and restarting the AppHost turns Google sign-in on without affecting the Dev scheme.

## Chrome extension

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Select **Load unpacked** and choose `src/chrome-extension/dist`.
3. After the watch build rebuilds, select the reload icon on the extension card. Popup changes appear the next time you open the popup.

The AppHost builds the extension against `https://localhost:7030/api/packets`. The API always runs on that port, so an extension you've loaded keeps working across runs.

## Mobile app

The app has the API's URL compiled in, as the device will reach it. By default that is your machine over the USB cable; the dev tunnel is only for a phone that is not on USB.

### A phone on USB, or an emulator

1. Fill in `src/mobile_app/.env` (`SetupDevEnvironment` creates it from `.env.example`): keep `API_BASE_URL=http://localhost:5218`, and set `SERVER_CLIENT_ID` (the web client id) and `APP_CLIENT_ID` (the Android client id).
2. Plug the phone in with USB debugging on, and make its port 5218 reach your machine:

   ```
   adb reverse tcp:5218 tcp:5218
   ```

   The forward lasts until the phone is unplugged or restarted, so run it again after that. With more than one device attached, add `-s <serial>` (`adb devices` lists them). An emulator needs no forward: set `API_BASE_URL=http://10.0.2.2:5218`, the address an emulator reaches its host by.
3. Run the **Mobile app (development)** launch configuration in VS Code, or run:

   ```
   cd src/mobile_app
   flutter run --flavor development --dart-define-from-file=.env
   ```

This works because the API also listens for plain HTTP on port 5218 (the `https` launch profile, see `Properties/launchSettings.json`), and the development flavor of the app allows plain HTTP to `localhost`, `127.0.0.1` and `10.0.2.2` only (`android/app/src/development/res/xml/network_security_config.xml`). The app checks the URL at start-up: the development flavor rejects an `http://` URL to any other host, and the production flavor rejects every `http://` URL, so a store build can never talk to the API in the clear. Nothing leaves your machine; the phone reaches the API through the cable.

The development flavor installs as **Passel Dev** (`com.invex.passel.mobileapp.dev`), alongside the production app. If a required value is missing, or a value can't be used in that flavor, the app opens on a configuration page that says which.

### A phone that is not on USB: the dev tunnel

The `passel-api-tunnel` resource gives the API a public HTTPS URL. It doesn't start with the AppHost: start it from the dashboard when you need it (the resource's **Start** action), and stop it when you are done.

1. The first time, sign in to Dev Tunnels: `devtunnel user login`. The dashboard also prompts you if you aren't signed in.
2. Copy the tunnel URL from the dashboard into `API_BASE_URL` in `src/mobile_app/.env`. The URL stays the same between runs.
3. Run the app as above. The URL is compiled in, so rebuild when you switch between the USB URL and the tunnel URL.

> [!WARNING]
> While the tunnel runs, anyone with its URL can reach your API, and it is the Development configuration they reach: every packet endpoint, with a Google ID token anyone can mint for their own account; the Development exception page with stack traces; the `Dev <user>` scheme, which signs in as any user without a password; and a database that Marten recreates freely. Scalar, the OpenAPI document and the browser login only answer on `localhost`, so they are not served through the tunnel. The URL is stable, so treat it like a credential: keep it out of screenshots and commits, and stop the tunnel when the phone is back on USB.

#### Requiring a token

To make the tunnel refuse anyone without a token, turn off anonymous access and give the app a token:

1. Set `DevTunnel:AllowAnonymous` to `false` for the AppHost: in its user secrets (`dotnet user-secrets set DevTunnel:AllowAnonymous false --project src/webapi/Invex.Passel.AppHost`), in its git-ignored `appsettings.Development.json`, or as the `DevTunnel__AllowAnonymous` environment variable. Restart the AppHost and start the tunnel.
2. Issue a token for the tunnel: `devtunnel token <tunnel-id> --scopes connect`. The tunnel id is the resource name followed by a short hash (`passel-api-tunnel-xxxxxxxx`); `devtunnel list` shows it, and it is also the start of the tunnel URL.
3. Put the token in `TUNNEL_ACCESS_TOKEN` in `.env` and rebuild the app. It sends `X-Tunnel-Authorization: tunnel <token>` with every request, and the tunnel checks it before forwarding. Tokens expire, so repeat the last two steps when the app starts reporting that the session has expired (the tunnel's 401 looks like one to the app).

The production flavor refuses to start with a tunnel token, since it is a development credential that would otherwise be compiled into a store build.
