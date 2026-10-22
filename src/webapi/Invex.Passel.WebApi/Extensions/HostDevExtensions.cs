namespace Invex.Passel.WebApi.Extensions;

internal static class HostDevExtensions
{
    [PublicAPI]
    public static void MapDevApi(this WebApplication app)
    {
        app
            .MapGet("/auth/login",
                () => Results.Challenge(new()
                    {
                        RedirectUri = "/scalar",
                    },
                    [GoogleOpenIdConnectDefaults.AuthenticationScheme]))
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

            options.AddHeadContent("""
                                   <script>
                                       document.addEventListener('DOMContentLoaded', function() {
                                           const banner = document.createElement('div');
                                           banner.className = 'login-banner';
                                           fetch('/auth/me').then(r => {
                                               if (r.ok) {
                                                   r.json().then(u => {
                                                       banner.innerHTML = 'Logged in as <strong>' + u.email + '</strong> &mdash; <a href="/auth/logout">Logout</a>';
                                                   });
                                               } else {
                                                   banner.innerHTML = '<a href="/auth/login">Login with Google</a> to use authenticated endpoints';
                                               }
                                               document.body.prepend(banner);
                                           });
                                       });
                                   </script>
                                   """);
        });
    }
}
