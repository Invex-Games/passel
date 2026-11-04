using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Google OAuth configuration, read from this project's user secrets (Parameters:<name>); see
// docs/getting-started/google-oauth.md. In Development the values are optional: left empty, the API still starts
// and accepts "Authorization: Dev <user>" instead of Google tokens (docs/getting-started/running-locally.md).
// Elsewhere they are required, and the dashboard prompts for any that are missing.
var googleOptional = builder.Environment.IsDevelopment();

var googleClientId = AddGoogleParameter("google-client-id")
    .WithDescription("Google OAuth **web** client id. The API's token audience and the mobile app's SERVER_CLIENT_ID.",
        enableMarkdown: true);

var googleClientSecret = AddGoogleParameter("google-client-secret", secret: true)
    .WithDescription("Secret of the Google OAuth web client. Only used for the development-only Scalar login.");

var extensionClientId = AddGoogleParameter("google-extension-client-id")
    .WithDescription("Google OAuth client id of the Chrome extension. Accepted by the API as an additional audience.");

var postgres = builder
    .AddPostgres("passel-pg")
    .WithPgWeb()
    .WithLifetime(ContainerLifetime.Persistent);

var postgresdb = postgres.AddDatabase("passel-db");

// The https profile pins https://localhost:7030, so the unpacked extension (whose host permissions are derived from
// API_URL) keeps working across runs
var webApi = builder
    .AddProject<Projects.Invex_Passel_WebApi>("invex-passel-webapi", launchProfileName: "https")
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithEnvironment("Authentication__Google__ClientId", googleClientId)
    .WithEnvironment("Authentication__Google__ClientSecret", googleClientSecret)
    .WithEnvironment("Authentication__Google__AdditionalAudiences__0", extensionClientId);

// Rebuilds src/chrome-extension/dist on every change; load that folder unpacked in Chrome. These variables take
// precedence over the extension's own .env file. The extension's API_URL is the packets endpoint, not the API root.
builder
    .AddJavaScriptApp("chrome-extension", "../../chrome-extension", "dev:watch")
    .WithEnvironment("API_URL", ReferenceExpression.Create($"{webApi.GetEndpoint("https")}/api/packets"))
    .WithEnvironment("GOOGLE_CLIENT_ID", extensionClientId)
    // The popup of this development build can also sign in as a development user (the API's Dev scheme)
    .WithEnvironment("DEV_SIGN_IN", "true");

// The web app's dev server, which rebuilds src/web/dist on every change and reloads the page. Its port is fixed and not
// proxied, so the origin stays http://localhost:5245: Google only signs in from the Authorized JavaScript origins of
// the web client, and the API lets the web app's origin through CORS. The web app signs in with the web client id,
// the API's main audience, and its development build can also sign in as a development user
var webApp = builder
    .AddJavaScriptApp("web-app", "../../web", "dev")
    .WithHttpEndpoint(port: 5245, env: "PORT", isProxied: false)
    .WithEnvironment("API_URL", ReferenceExpression.Create($"{webApi.GetEndpoint("https")}/api/packets"))
    .WithEnvironment("GOOGLE_CLIENT_ID", googleClientId)
    .WithEnvironment("DEV_SIGN_IN", "true");

webApi.WithEnvironment("Cors__AllowedOrigins__0", webApp.GetEndpoint("http"));

// Gives a phone that is not on USB a stable public URL for the API, to use as the mobile app's API_BASE_URL.
// It only runs when started from the dashboard: while it runs, anyone with the URL can reach the Development
// API (docs/getting-started/running-locally.md says what that exposes). A phone on USB and an emulator use
// `adb reverse` and http://localhost:5218 instead and need no tunnel. Needs the devtunnel CLI and a one-off
// `devtunnel user login`.
//
// Anonymous by default, so the phone needs nothing but the URL. Set DevTunnel:AllowAnonymous to false (in this
// project's user secrets, its git-ignored appsettings.Development.json, or the DevTunnel__AllowAnonymous
// environment variable) to require a Dev Tunnels access token instead; the app sends one from its
// TUNNEL_ACCESS_TOKEN dart-define.
var tunnel = builder
    .AddDevTunnel("passel-api-tunnel")
    .WithReference(webApi.GetEndpoint("https"))
    .WithExplicitStart();

if (builder.Configuration.GetValue("DevTunnel:AllowAnonymous", true))
    tunnel.WithAnonymousAccess();

builder
    .Build()
    .Run();

IResourceBuilder<ParameterResource> AddGoogleParameter(string name, bool secret = false) =>
    googleOptional
        ? builder.AddParameter(name, new EmptyParameterDefault(), secret: secret)
        : builder.AddParameter(name, secret: secret);

/// <summary>
///     Resolves a parameter to an empty value when configuration has none, so nothing prompts for it. Writes no
///     default to a manifest, where the value stays required.
/// </summary>
internal sealed class EmptyParameterDefault : ParameterDefault
{
    public override string GetDefaultValue() =>
        string.Empty;

    public override void WriteToManifest(ManifestPublishingContext context)
    {
    }
}
