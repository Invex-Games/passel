using System.Net;
using System.Security.Claims;
using Invex.Passel.WebApi.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The Scalar page served by the real Development authentication setup, with Google login configured. A
///     visitor who is not logged in gets the Dev scheme filled in; one who is logged in with Google gets it empty,
///     so the page sends the login cookie rather than a Dev header that would win over it.
/// </summary>
public sealed class ScalarDevSchemeTests : IAsyncLifetime
{
    /// <summary>
    ///     The filled-in Dev scheme, as Scalar serializes it into the page.
    /// </summary>
    private const string PrefilledValue = "\"value\":\"Dev alice\"";

    private const string PreferredScheme = "\"preferredSecurityScheme\":[\"Dev\"]";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();

        // Enough for the cookie and Google schemes to be registered; Google itself is never contacted
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:ClientId"] = "web-client-id",
            ["Authentication:Google:ClientSecret"] = "web-client-secret",
        });

        builder.AddWebDefaults();

        _app = builder.Build();
        _app.UseWebDefaults();
        _app.MapDevApi();

        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task The_login_cookie_is_accepted()
    {
        using var anonymous = await _client.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add("Cookie", LoginCookie());
        using var loggedIn = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        Assert.Contains("declan@example.com", await loggedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_visitor_who_is_not_logged_in_gets_the_Dev_scheme_filled_in()
    {
        var page = await GetScalarPage(loggedIn: false);

        Assert.Contains(PrefilledValue, page);
        Assert.Contains(PreferredScheme, page);
    }

    [Fact]
    public async Task A_visitor_logged_in_with_Google_gets_the_Dev_scheme_empty()
    {
        var page = await GetScalarPage(loggedIn: true);

        Assert.DoesNotContain(PrefilledValue, page);
        Assert.DoesNotContain(PreferredScheme, page);

        // The banner still shows the example, for a developer who wants to act as a development user
        Assert.Contains(HostDevExtensions.DevSchemeExample, page);
    }

    [Fact]
    public async Task The_decision_is_made_for_every_request()
    {
        // The page is configured per request, so one visitor's login must not leak into the next page served
        Assert.Contains(PrefilledValue, await GetScalarPage(loggedIn: false));
        Assert.DoesNotContain(PrefilledValue, await GetScalarPage(loggedIn: true));
        Assert.Contains(PrefilledValue, await GetScalarPage(loggedIn: false));
    }

    private async Task<string> GetScalarPage(bool loggedIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/scalar/");

        if (loggedIn)
            request.Headers.Add("Cookie", LoginCookie());

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A cookie the cookie scheme accepts, protected the way the Google login would have protected it.
    /// </summary>
    private string LoginCookie()
    {
        var options = _app
            .Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "1234567890"),
                new Claim(ClaimTypes.Name, "Declan"),
                new Claim(ClaimTypes.Email, "declan@example.com"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            CookieAuthenticationDefaults.AuthenticationScheme);

        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }
}
