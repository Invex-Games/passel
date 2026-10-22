using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostWebExtensions
{
    private const string DevelopmentAuthenticationScheme = "Smart";
    private const string ForwardedHeadersEnabledKey = "ForwardedHeaders:Enabled";

    /// <summary>
    ///     Kestrel allows unlimited connections by default. Each event stream holds one, so a limit keeps a flood
    ///     of them from exhausting the process.
    /// </summary>
    private const long DefaultMaxConcurrentConnections = 5000;

    public static void AddWebDefaults(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            // Clients aren't required to send the "$type" discriminator as the first property
            options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
        builder.AddPacketRateLimiting();
        builder.AddConnectionLimits();
        builder.AddForwardedHeaders();
        builder.AddHostFiltering();

        // No CORS: the extension's host permissions bypass it and the mobile app is not a browser. The web app will
        // add an allowlist of its own origin when it arrives.

        // Clients authenticate with Google ID tokens. Cookie + Google OIDC browser login only exists in
        // development so Scalar can call authenticated endpoints; in production, unauthenticated API
        // requests get a plain 401 rather than a redirect to Google.
        var isDevelopment = builder.Environment.IsDevelopment();

        var authentication = builder
            .Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = isDevelopment
                    ? DevelopmentAuthenticationScheme
                    : JwtBearerDefaults.AuthenticationScheme;

                options.DefaultChallengeScheme = options.DefaultScheme;
            })
            .AddJwtBearer(options =>
            {
                var clientId = builder.Configuration["Authentication:Google:ClientId"]!;

                var additionalAudiences = builder
                    .Configuration
                    .GetSection("Authentication:Google:AdditionalAudiences")
                    .Get<string[]>() ?? [];

                options.Authority = "https://accounts.google.com";

                options.TokenValidationParameters = new()
                {
                    ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                    ValidAudiences = [clientId, ..additionalAudiences],
                };
            });

        if (isDevelopment)
            authentication
                .AddPolicyScheme(DevelopmentAuthenticationScheme,
                    "Cookie or Bearer",
                    options =>
                    {
                        options.ForwardDefaultSelector = ctx => ctx.Request.Headers.ContainsKey("Authorization")
                            ? JwtBearerDefaults.AuthenticationScheme
                            : CookieAuthenticationDefaults.AuthenticationScheme;
                    })
                .AddCookie(cookieOptions =>
                {
                    cookieOptions.ForwardChallenge = GoogleOpenIdConnectDefaults.AuthenticationScheme;
                    cookieOptions.ForwardForbid = GoogleOpenIdConnectDefaults.AuthenticationScheme;

                    // Only ever sent back to this origin over HTTPS; Scalar lives on the same origin, so Strict
                    // costs nothing
                    cookieOptions.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    cookieOptions.Cookie.SameSite = SameSiteMode.Strict;
                })
                .AddGoogleOpenIdConnect(googleOptions =>
                {
                    googleOptions.ClientId = builder.Configuration["Authentication:Google:ClientId"];
                    googleOptions.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];

                    // The correlation and nonce cookies only need to come back with the callback from Google. Lax
                    // cookies ride along with a top-level GET, so the code is returned in the query rather than
                    // by the form post the OIDC handler would otherwise ask for
                    googleOptions.ResponseMode = OpenIdConnectResponseMode.Query;
                    googleOptions.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    googleOptions.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                    googleOptions.NonceCookie.SameSite = SameSiteMode.Lax;
                    googleOptions.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                });

        builder.Services.AddAuthorization();

        builder
            .Services
            .AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = ApiVersion.Default;
            })
            .AddApiExplorer(options => options.GroupNameFormat = "'v'VVV")
            .AddOpenApi();
    }

    private static void AddConnectionLimits(this WebApplicationBuilder builder)
    {
        // Binds the whole Kestrel section, so every limit can be set in configuration, then applies the default
        builder.Services.Configure<KestrelServerOptions>(builder.Configuration.GetSection("Kestrel"));

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Limits.MaxConcurrentConnections ??= DefaultMaxConcurrentConnections);
    }

    /// <summary>
    ///     Behind a reverse proxy, takes the client address and scheme from <c>X-Forwarded-For</c> and
    ///     <c>X-Forwarded-Proto</c>, so rate limits key on the real client and HSTS sees HTTPS.
    /// </summary>
    private static void AddForwardedHeaders(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("ForwardedHeaders");

        if (!section.GetValue<bool>("Enabled"))
            return;

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // With no proxies listed, the headers are trusted from whatever connects directly, which is only safe
            // when the proxy is the one way to reach the API
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));

            foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        });
    }

    private static void AddHostFiltering(this WebApplicationBuilder builder)
    {
        // appsettings.json names the production host. Locally the API is reached as localhost and through the dev
        // tunnel, whose host differs per developer, so Development answers to any host
        if (builder.Environment.IsDevelopment())
            builder.Services.PostConfigure<HostFilteringOptions>(options => options.AllowedHosts = ["*"]);
    }

    public static void UseWebDefaults(this WebApplication app)
    {
        // First, so everything after it sees the real client address and scheme
        if (app.Configuration.GetValue<bool>(ForwardedHeadersEnabledKey))
            app.UseForwardedHeaders();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        app.UseResponseCompression();
        app.UseAuthentication();

        // After authentication, so limits are counted per user rather than per address
        app.UseRateLimiter();
        app.UseAuthorization();
    }
}
