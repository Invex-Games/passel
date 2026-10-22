using Aspire.Hosting.Publishing;
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
    .WithEnvironment("GOOGLE_CLIENT_ID", extensionClientId);

// Gives physical devices a stable public URL for the API; use it as the mobile app's API_BASE_URL.
// Needs the devtunnel CLI, and a one-off `devtunnel user login`.
builder
    .AddDevTunnel("passel-api-tunnel")
    .WithReference(webApi.GetEndpoint("https"))
    .WithAnonymousAccess();

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
