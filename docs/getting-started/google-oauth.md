# Google OAuth Clients

Every Passel client signs in with Google and sends the resulting **ID token** to the API as a bearer token. The API accepts a token only if its audience (`aud`) is one it knows about. That makes the client ids the one piece of configuration all three apps share.

## The clients

Create these in a Google Cloud project, under **APIs & Services → Credentials**. If you're joining the team, ask for the existing ids instead of creating new ones.

| Client | Google client type | Setup | Used by |
|---|---|---|---|
| **Web client** | Web application | Authorized redirect URI `https://localhost:7030/signin-oidc`, used by the development-only Scalar login | The API (its main audience), the mobile app's `SERVER_CLIENT_ID` |
| **Extension client** | Web application | Authorized redirect URI `https://aikjdmilnbdacmaeahelifppjjgppemj.chromiumapp.org/` | The extension's `GOOGLE_CLIENT_ID`; the API accepts it as an additional audience |
| **Android client** | Android | Package `com.invex.passel.mobileapp.dev` (development flavor) and the SHA-1 of your signing certificate (the debug keystore for local builds) | The mobile app's `APP_CLIENT_ID` |

The extension id `aikjdmilnbdacmaeahelifppjjgppemj` comes from the `key` field in `src/chrome-extension/static/manifest.json`. Don't change that key: the id, and with it the redirect URI, would change.

The mobile app asks Google for a token for the **web** client (`serverClientId`), which is why it needs no audience of its own.

## Giving the values to the AppHost

The AppHost declares three parameters:

| Parameter | Value |
|---|---|
| `google-client-id` | The web client id |
| `google-client-secret` | The web client secret (secret parameter) |
| `google-extension-client-id` | The extension client id |

The AppHost reads them from its user secrets. In Development they're optional, so the dashboard doesn't ask for them: without them the API runs with the Dev scheme described in [Run without Google](running-locally.md#run-without-google). Set the ones you have with:

```
dotnet user-secrets --project src/webapi/Invex.Passel.AppHost set Parameters:google-client-id <web client id>
dotnet user-secrets --project src/webapi/Invex.Passel.AppHost set Parameters:google-client-secret <web client secret>
dotnet user-secrets --project src/webapi/Invex.Passel.AppHost set Parameters:google-extension-client-id <extension client id>
```

The AppHost passes these values to the API and to the extension's watch build, so the API and extension need no local config files. The mobile app is built separately, so its ids still go in `src/mobile_app/.env`; see [Running Locally](running-locally.md#mobile-app).
