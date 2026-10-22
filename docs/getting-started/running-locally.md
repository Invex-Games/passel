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
| `passel-api-tunnel` | A dev tunnel giving the API a public HTTPS URL that physical devices can reach. |

If the dashboard shows unresolved parameters, enter the Google client values; see [Google OAuth Clients](google-oauth.md).

## Web API

- Scalar API reference: `https://localhost:7030/scalar`. Use the **Login with Google** banner to call authenticated endpoints from the browser.
- Health checks: `/health` (every check, including the database) and `/alive` (liveness only).
- Logs, traces and metrics: the dashboard.

> [!WARNING]
> In development, Marten recreates any table whose schema doesn't match the code (`AutoCreate.All`). Expect local data to be dropped when you change the document model.

To run the API on its own, outside the AppHost, use its `https` launch profile. You then need to supply a `passel-db` connection string, and the `Authentication:Google:*` settings in `src/webapi/Invex.Passel.WebApi/appsettings.Development.json` (git-ignored). See [Configuration](../reference/configuration.md#web-api).

## Chrome extension

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Select **Load unpacked** and choose `src/chrome-extension/dist`.
3. After the watch build rebuilds, select the reload icon on the extension card. Popup changes appear the next time you open the popup.

The AppHost builds the extension against `https://localhost:7030/api/packets`. The API always runs on that port, so an extension you've loaded keeps working across runs.

## Mobile app

A phone can't reach `localhost`, so the app talks to the API through the dev tunnel.

1. The first time, sign in to Dev Tunnels: `devtunnel user login`. The dashboard also prompts you if you aren't signed in.
2. Copy the `passel-api-tunnel` URL from the dashboard into `API_BASE_URL` in `src/mobile_app/.env`. The URL stays the same between runs, so you only do this once.
3. Fill in `SERVER_CLIENT_ID` (the web client id) and `APP_CLIENT_ID` (the Android client id).
4. Run the **Mobile app (development)** launch configuration in VS Code, or run:

   ```
   cd src/mobile_app
   flutter run --flavor development --dart-define-from-file=.env
   ```

The development flavor installs as **Passel Dev** (`com.invex.passel.mobileapp.dev`), alongside the production app. If any required value is missing, the app opens on a "missing configuration" page that lists it.

> [!NOTE]
> The tunnel allows anonymous access, so while the AppHost is running your local API is reachable from the internet. Every packet endpoint still requires a valid Google ID token.
