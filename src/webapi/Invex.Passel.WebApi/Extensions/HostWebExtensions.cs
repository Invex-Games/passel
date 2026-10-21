namespace Invex.Passel.WebApi.Extensions;

internal static class HostWebExtensions
{
    private const string DevelopmentAuthenticationScheme = "Smart";

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

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()));

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
                })
                .AddGoogleOpenIdConnect(googleOptions =>
                {
                    googleOptions.ClientId = builder.Configuration["Authentication:Google:ClientId"];
                    googleOptions.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
                    googleOptions.ProtocolValidator.RequireNonce = false;
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

    public static void UseWebDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseResponseCompression();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
