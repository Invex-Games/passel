using Microsoft.AspNetCore.OpenApi;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostDevExtensions
{
    [PublicAPI]
    public static void MapDevApi(this WebApplication app)
    {
        var googleLoginConfigured = HostWebExtensions.GoogleLoginConfigured(app.Configuration);

        app
            .MapGet("/auth/login",
                () => googleLoginConfigured
                    ? Results.Challenge(new()
                        {
                            RedirectUri = "/scalar",
                        },
                        [GoogleOpenIdConnectDefaults.AuthenticationScheme])
                    : GoogleLoginNotConfigured())
            .ExcludeFromDescription();

        app
            .MapGet("/auth/logout",
                async (HttpContext ctx) =>
                {
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    return Results.Redirect("/scalar");
                })
            .ExcludeFromDescription();

        app
            .MapGet("/auth/me",
                (HttpContext ctx) => ctx.User.Identity?.IsAuthenticated is true
                    ? Results.Ok(new
                    {
                        Name = ctx.User.FindFirstValue(ClaimTypes.Name),
                        Email = ctx.User.FindFirstValue(ClaimTypes.Email),
                    })
                    : Results.Unauthorized())
            .ExcludeFromDescription();

        app
            .MapOpenApi()
            .WithDocumentPerVersion();

        app.MapScalarApiReference(options =>
        {
            var descriptions = app.DescribeApiVersions();

            for (var i = 0; i < descriptions.Count; i++)
            {
                var description = descriptions[i];
                var isDefault = i == descriptions.Count - 1;

                options.AddDocument(description.GroupName, description.GroupName, isDefault: isDefault);
            }

            // The Dev scheme is declared in the document by AddDevSecurityScheme; Scalar offers it first, with a
            // value ready to send
            options.AddPreferredSecuritySchemes([DevAuthenticationHandler.SchemeName]);
            options.AddApiKeyAuthentication(DevAuthenticationHandler.SchemeName, scheme => scheme.WithValue("Dev alice"));

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
                ? """<a href="/auth/login">Login with Google</a> to use authenticated endpoints, or authorize with the <strong>Dev</strong> scheme (<code>Dev alice</code>)"""
                : """Google login is not configured (set <code>Authentication:Google:ClientId</code> and <code>ClientSecret</code>); authorize with the <strong>Dev</strong> scheme instead (<code>Dev alice</code>)""";

            options.AddHeadContent($$"""
                                     <script>
                                         document.addEventListener('DOMContentLoaded', function() {
                                             const banner = document.createElement('div');
                                             banner.className = 'login-banner';
                                             fetch('/auth/me').then(r => {
                                                 if (r.ok) {
                                                     r.json().then(u => {
                                                         banner.innerHTML = 'Logged in as <strong>' + (u.email || u.name) + '</strong> &mdash; <a href="/auth/logout">Logout</a>';
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
    ///     Declares the Dev scheme in the OpenAPI document, so Scalar offers it and sends its header.
    /// </summary>
    internal static Task AddDevSecurityScheme(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[DevAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Development only: \"Dev <user-id>\" signs the request in as that user.",
        };

        document.Security ??= [];

        document.Security.Add(new()
        {
            [new OpenApiSecuritySchemeReference(DevAuthenticationHandler.SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }

    private static IResult GoogleLoginNotConfigured() =>
        Results.Problem(
            "Google login is not configured. Set Authentication:Google:ClientId and ClientSecret, or send Authorization: Dev <user> instead.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Google login is not configured");
}
