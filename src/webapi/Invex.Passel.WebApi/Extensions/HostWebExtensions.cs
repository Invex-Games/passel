using System.Net;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;
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

            // ...and a body without it gets a 400, like any other body the API cannot read
            options.SerializerOptions.TypeInfoResolver = (options.SerializerOptions.TypeInfoResolver ??
                                                          new DefaultJsonTypeInfoResolver())
                .WithAddedModifier(RequireTypeDiscriminator);
        });

        // Development would otherwise throw on a body it can't read, and the exception handler would answer 500; this
        // way every environment answers 400, which the status code pages turn into a problem
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
        builder.AddPacketRateLimiting();
        builder.AddConnectionLimits();
        builder.AddForwardedHeaders();
        builder.AddHostFiltering();

        // No CORS: the extension's host permissions bypass it and the mobile app is not a browser. The web app will
        // add an allowlist of its own origin when it arrives.

        builder.AddPasselAuthentication();
        builder.Services.AddAuthorization();

        var isDevelopment = builder.Environment.IsDevelopment();

        builder
            .Services
            .AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = ApiVersion.Default;
            })
            .AddApiExplorer(options => options.GroupNameFormat = "'v'VVV")
            .AddOpenApi(options =>
            {
                options.Document.AddDocumentTransformer(DescribeApi);

                // Lets Scalar offer the Development schemes; the document is only served in Development anyway
                if (isDevelopment)
                    options.Document.AddDocumentTransformer(HostDevExtensions.AddDevelopmentSecuritySchemes);
            });
    }

    /// <summary>
    ///     Makes a union body without its <c>$type</c> discriminator unreadable JSON. System.Text.Json cannot create
    ///     the abstract base of a union and says so with a <see cref="NotSupportedException" />, which body binding
    ///     does not count as a bad request, so it reached the exception handler as a 500. A
    ///     <see cref="JsonException" /> gets the 400 of any other body the API cannot read.
    /// </summary>
    internal static void RequireTypeDiscriminator(JsonTypeInfo typeInfo)
    {
        if (typeInfo is { Kind: JsonTypeInfoKind.Object, PolymorphismOptions: not null, CreateObject: null } &&
            typeInfo.Type.IsAbstract)
            typeInfo.CreateObject = () =>
                throw new JsonException($"A {typeInfo.Type.Name} must say what it is with a \"$type\" discriminator.");
    }

    /// <summary>
    ///     The document's title, and the scheme every environment accepts: a Google ID token as a bearer token. The
    ///     Development host adds its own schemes after this. The document is committed as
    ///     <c>contracts/openapi.json</c>, so what this writes is part of the contract the integration tests check.
    /// </summary>
    private static Task DescribeApi(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info ??= new();
        document.Info.Title = "Passel API";
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Security ??= [];

        document.Components.SecuritySchemes[JwtBearerDefaults.AuthenticationScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "A Google ID token issued for one of the API's client ids.",
        };

        document.Security.Add(new()
        {
            [new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, document)] = [],
        });

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether the development browser login can work: it needs the Google web client id and its secret.
    /// </summary>
    internal static bool GoogleLoginConfigured(IConfiguration configuration) =>
        !string.IsNullOrEmpty(configuration["Authentication:Google:ClientId"]) &&
        !string.IsNullOrEmpty(configuration["Authentication:Google:ClientSecret"]);

    private static void AddPasselAuthentication(this WebApplicationBuilder builder)
    {
        // Clients authenticate with Google ID tokens. In Development a policy scheme also accepts
        // "Authorization: Dev <user>", and a cookie issued by Google OpenID Connect login when the Google client is
        // configured, so Scalar can call authenticated endpoints. Production only has the bearer handler, so
        // unauthenticated requests get a plain 401 rather than a redirect to Google.
        var isDevelopment = builder.Environment.IsDevelopment();
        var googleLoginConfigured = isDevelopment && GoogleLoginConfigured(builder.Configuration);

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
                var additionalAudiences = builder
                    .Configuration
                    .GetSection("Authentication:Google:AdditionalAudiences")
                    .Get<string[]>() ?? [];

                options.Authority = "https://accounts.google.com";

                // Without a client id (allowed in Development) the audience list is empty and every token is
                // rejected, which is the right way to fail
                options.TokenValidationParameters = new()
                {
                    ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                    ValidAudiences = ValidAudiences(builder.Configuration["Authentication:Google:ClientId"],
                        additionalAudiences),
                };
            });

        if (!isDevelopment)
            return;

        authentication
            .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthenticationHandler.SchemeName,
                "Development user",
                _ => { })
            .AddPolicyScheme(DevelopmentAuthenticationScheme,
                "Dev, Bearer or Cookie",
                options => options.ForwardDefaultSelector = ctx =>
                    SelectDevelopmentScheme(ctx.Request.Headers.Authorization, googleLoginConfigured))
            .AddCookie(cookieOptions =>
            {
                if (googleLoginConfigured)
                {
                    cookieOptions.ForwardChallenge = GoogleOpenIdConnectDefaults.AuthenticationScheme;
                    cookieOptions.ForwardForbid = GoogleOpenIdConnectDefaults.AuthenticationScheme;
                }

                // Only ever sent back to this origin over HTTPS; Scalar lives on the same origin, so Strict
                // costs nothing
                cookieOptions.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookieOptions.Cookie.SameSite = SameSiteMode.Strict;
            });

        if (!googleLoginConfigured)
            return;

        authentication.AddGoogleOpenIdConnect(googleOptions =>
        {
            googleOptions.ClientId = builder.Configuration["Authentication:Google:ClientId"];
            googleOptions.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];

            // The correlation and nonce cookies only need to come back with the callback from Google. Lax cookies
            // ride along with a top-level GET, so the code is returned in the query rather than by the form post
            // the OIDC handler would otherwise ask for
            googleOptions.ResponseMode = OpenIdConnectResponseMode.Query;
            googleOptions.CorrelationCookie.SameSite = SameSiteMode.Lax;
            googleOptions.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            googleOptions.NonceCookie.SameSite = SameSiteMode.Lax;
            googleOptions.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        });
    }

    /// <summary>
    ///     Which scheme authenticates a request in Development: the Dev scheme for its own header, bearer tokens for
    ///     any other Authorization header, and otherwise the cookie login when Google is configured, or the Dev
    ///     scheme (whose challenge is a plain 401) when it is not.
    /// </summary>
    internal static string SelectDevelopmentScheme(StringValues authorization, bool googleLoginConfigured)
    {
        if (DevAuthenticationHandler.IsDevAuthorization(authorization))
            return DevAuthenticationHandler.SchemeName;

        if (!StringValues.IsNullOrEmpty(authorization))
            return JwtBearerDefaults.AuthenticationScheme;

        return googleLoginConfigured
            ? CookieAuthenticationDefaults.AuthenticationScheme
            : DevAuthenticationHandler.SchemeName;
    }

    /// <summary>
    ///     The audiences a bearer token may carry: the web client id and the extras, skipping anything blank.
    /// </summary>
    internal static string[] ValidAudiences(string? clientId, IEnumerable<string?> additionalAudiences)
    {
        var audiences = new List<string>();

        foreach (var audience in additionalAudiences.Prepend(clientId))
            if (!string.IsNullOrWhiteSpace(audience))
                audiences.Add(audience);

        return [..audiences];
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
