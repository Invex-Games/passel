using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostDevExtensions
{
    /// <summary>
    ///     The only host the development endpoints answer to, on any port. Through the dev tunnel the API is
    ///     addressed by the tunnel's host, so Scalar, the OpenAPI document and the browser login are not served to
    ///     the internet while the tunnel runs; the packet endpoints are, which is what the tunnel is for.
    /// </summary>
    internal const string DevelopmentHost = "localhost";

    [PublicAPI]
    public static void MapDevApi(this WebApplication app)
    {
        var googleLoginConfigured = HostWebExtensions.GoogleLoginConfigured(app.Configuration);

        // One group, so every development endpoint carries the host restriction
        var dev = app
            .MapGroup(string.Empty)
            .RequireHost(DevelopmentHost);

        dev
            .MapGet("/auth/login",
                () => googleLoginConfigured
                    ? Results.Challenge(new()
                        {
                            RedirectUri = "/scalar",
                        },
                        [GoogleOpenIdConnectDefaults.AuthenticationScheme])
                    : GoogleLoginNotConfigured())
            .ExcludeFromDescription();

        dev
            .MapGet("/auth/logout",
                async (HttpContext ctx) =>
                {
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    return Results.Redirect("/scalar");
                })
            .ExcludeFromDescription();

        dev
            .MapGet("/auth/me",
                (HttpContext ctx) => ctx.User.Identity?.IsAuthenticated is true
                    ? Results.Ok(new
                    {
                        Name = ctx.User.FindFirstValue(ClaimTypes.Name),
                        Email = ctx.User.FindFirstValue(ClaimTypes.Email),
                    })
                    : Results.Unauthorized())
            .ExcludeFromDescription();

        dev
            .MapOpenApi()
            .WithDocumentPerVersion();

        // Configured per request: what the page sends depends on whether the visitor is logged in
        dev.MapScalarApiReference((options, httpContext) =>
        {
            var descriptions = app.DescribeApiVersions();

            for (var i = 0; i < descriptions.Count; i++)
            {
                var description = descriptions[i];
                var isDefault = i == descriptions.Count - 1;

                options.AddDocument(description.GroupName, description.GroupName, isDefault: isDefault);
            }

            // The visitor is logged in only through the Google login cookie: a browser sends no Authorization
            // header to this page. The page then selects that login rather than the Dev scheme, since a Dev
            // header would win over the cookie (HostWebExtensions.SelectDevelopmentScheme)
            var loggedIn = httpContext.User.Identity?.IsAuthenticated is true;

            SelectSecurityScheme(options, loggedIn);

            options.CustomCss = """
                                .login-banner {
                                    background: #1a1a2e;
                                    padding: 8px 16px;
                                    text-align: center;
                                    font-size: 14px;
                                    color: #e2e8f0;
                                    border-bottom: 1px solid #2d3748;
                                }
                                .login-banner a {
                                    color: #63b3ed;
                                    text-decoration: none;
                                    font-weight: 500;
                                }
                                .login-banner a:hover {
                                    text-decoration: underline;
                                }
                                """;

            var signedOutBanner = googleLoginConfigured
                ? $"""<a href="/auth/login">Login with Google</a> to use authenticated endpoints, or authorize with the <strong>Dev</strong> scheme (<code>{DevSchemeExample}</code>)"""
                : $"""Google login is not configured (set <code>Authentication:Google:ClientId</code> and <code>ClientSecret</code>); authorize with the <strong>Dev</strong> scheme instead (<code>{DevSchemeExample}</code>)""";

            // Requests go as the login unless the developer picks the Dev scheme on purpose
            var signedInBanner =
                $"""Scalar sends that login (the <strong>{GoogleLoginSchemeName}</strong> scheme); to act as a development user instead, pick the <strong>Dev</strong> scheme (<code>{DevSchemeExample}</code>). <a href="/auth/logout">Logout</a>""";

            options.AddHeadContent($$"""
                                     <script>
                                         document.addEventListener('DOMContentLoaded', function() {
                                             const banner = document.createElement('div');
                                             banner.className = 'login-banner';
                                             fetch('/auth/me').then(r => {
                                                 if (r.ok) {
                                                     r.json().then(u => {
                                                         banner.innerHTML = 'Logged in as <strong>' + (u.email || u.name) + '</strong>. {{signedInBanner}}';
                                                     });
                                                 } else {
                                                     banner.innerHTML = '{{signedOutBanner}}';
                                                 }
                                                 document.body.prepend(banner);
                                             });
                                         });
                                     </script>
                                     """);
        });
    }

    /// <summary>
    ///     The Dev scheme value Scalar sends for a visitor who is not logged in, and the example the banner shows.
    /// </summary>
    internal const string DevSchemeExample = "Dev alice";

    /// <summary>
    ///     The security scheme the OpenAPI document declares for the Google login cookie, so Scalar can show and
    ///     select it. The browser sends the cookie by itself; the scheme is there to be seen, not filled in.
    /// </summary>
    internal const string GoogleLoginSchemeName = "GoogleLogin";

    /// <summary>
    ///     Selects the scheme Scalar sends with: the Google login for a logged-in visitor, otherwise the Dev scheme
    ///     with <see cref="DevSchemeExample" /> ready to go.
    /// </summary>
    internal static void SelectSecurityScheme(ScalarOptions options, bool loggedIn)
    {
        if (loggedIn)
        {
            options.AddPreferredSecuritySchemes([GoogleLoginSchemeName]);

            return;
        }

        options.AddPreferredSecuritySchemes([DevAuthenticationHandler.SchemeName]);
        options.AddApiKeyAuthentication(DevAuthenticationHandler.SchemeName, scheme => scheme.WithValue(DevSchemeExample));
    }

    /// <summary>
    ///     Declares the Development schemes in the OpenAPI document, so Scalar offers them: the Dev scheme, and the
    ///     Google login cookie when Google login is configured. Either one satisfies the document's requirement.
    /// </summary>
    internal static Task AddDevelopmentSecuritySchemes(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Security ??= [];

        document.Components.SecuritySchemes[DevAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Development only: \"Dev <user-id>\" signs the request in as that user.",
        };

        document.Security.Add(new()
        {
            [new OpenApiSecuritySchemeReference(DevAuthenticationHandler.SchemeName, document)] = [],
        });

        var services = context.ApplicationServices;

        if (!HostWebExtensions.GoogleLoginConfigured(services.GetRequiredService<IConfiguration>()))
            return Task.CompletedTask;

        var cookieName = services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme)
            .Cookie
            .Name ?? CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme;

        document.Components.SecuritySchemes[GoogleLoginSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = cookieName,
            Description = "Development only: your Google login, from \"Login with Google\" in the banner. The browser " +
                          "sends the cookie with every request from this page, so there is nothing to enter.",
        };

        document.Security.Add(new()
        {
            [new OpenApiSecuritySchemeReference(GoogleLoginSchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }

    private static IResult GoogleLoginNotConfigured() =>
        Results.Problem(
            "Google login is not configured. Set Authentication:Google:ClientId and ClientSecret, or send Authorization: Dev <user> instead.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Google login is not configured");
}
