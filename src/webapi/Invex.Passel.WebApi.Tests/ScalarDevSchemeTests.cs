using System.Net;
using System.Security.Claims;
using System.Text.Json;
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
///     The Scalar page and the OpenAPI document served by the real Development authentication setup. The document
///     always declares the bearer token; with Google login configured it adds the Dev scheme and the Google login
///     (otherwise the Dev scheme alone); a visitor who is not logged in
///     gets the Dev scheme filled in, one who is logged in with Google gets the login selected, so the page sends
///     the login cookie rather than a Dev header that would win over it.
/// </summary>
public sealed class ScalarDevSchemeTests : IAsyncLifetime
{
    /// <summary>
    ///     The filled-in Dev scheme, as Scalar serializes it into the page.
    /// </summary>
    private const string PrefilledValue = "\"value\":\"Dev alice\"";

    private const string DevPreferred = "\"preferredSecurityScheme\":[\"Dev\"]";
    private const string GoogleLoginPreferred = "\"preferredSecurityScheme\":[\"GoogleLogin\"]";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await StartHost(googleLoginConfigured: true);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    ///     The Development host as <c>Program.cs</c> builds it, minus the database. With the Google client
    ///     configured the cookie and Google schemes are registered too; Google itself is never contacted.
    /// </summary>
    private static async Task<WebApplication> StartHost(bool googleLoginConfigured)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();

        // Set either way, so a developer machine's own Google settings (environment, user secrets, the
        // git-ignored appsettings.Development.json copied next to the tests) never decide the outcome
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:ClientId"] = googleLoginConfigured ? "web-client-id" : "",
            ["Authentication:Google:ClientSecret"] = googleLoginConfigured ? "web-client-secret" : "",
        });

        builder.AddWebDefaults();

        var app = builder.Build();
        app.UseWebDefaults();
        app.MapDevApi();

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }

    [Fact]
    public async Task The_login_cookie_is_accepted()
    {
        using var anonymous = await _client.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add("Cookie", LoginCookie(_app));
        using var loggedIn = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        Assert.Contains("declan@example.com", await loggedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_visitor_who_is_not_logged_in_gets_the_Dev_scheme_filled_in()
    {
        var page = await GetScalarPage(loggedIn: false);

        Assert.Contains(PrefilledValue, page);
        Assert.Contains(DevPreferred, page);
        Assert.DoesNotContain(GoogleLoginPreferred, page);
    }

    [Fact]
    public async Task A_visitor_logged_in_with_Google_gets_the_login_selected_and_the_Dev_scheme_empty()
    {
        var page = await GetScalarPage(loggedIn: true);

        Assert.Contains(GoogleLoginPreferred, page);
        Assert.DoesNotContain(PrefilledValue, page);
        Assert.DoesNotContain(DevPreferred, page);

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

    [Fact]
    public async Task The_document_declares_the_bearer_token_the_Dev_scheme_and_the_Google_login()
    {
        using var document = await GetDocument(_client);

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");

        // The bearer token is the scheme every environment accepts; the other two exist in Development only
        var bearer = schemes.GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());

        Assert.Equal("header", schemes.GetProperty("Dev").GetProperty("in").GetString());

        var login = schemes.GetProperty(HostDevExtensions.GoogleLoginSchemeName);
        Assert.Equal("apiKey", login.GetProperty("type").GetString());
        Assert.Equal("cookie", login.GetProperty("in").GetString());
        Assert.Equal(".AspNetCore.Cookies", login.GetProperty("name").GetString());

        // Any one scheme satisfies the document
        var requirements = document
            .RootElement
            .GetProperty("security")
            .EnumerateArray()
            .Select(requirement => requirement.EnumerateObject().Single().Name)
            .ToList();

        Assert.Equal(["Bearer", "Dev", HostDevExtensions.GoogleLoginSchemeName], requirements);
    }

    [Fact]
    public async Task Without_Google_login_the_document_declares_the_bearer_token_and_the_Dev_scheme()
    {
        await using var app = await StartHost(googleLoginConfigured: false);
        using var client = app.GetTestClient();

        using var document = await GetDocument(client);

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal(["Bearer", "Dev"], schemes.EnumerateObject().Select(scheme => scheme.Name).ToList());
    }

    private static async Task<JsonDocument> GetDocument(HttpClient client)
    {
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<string> GetScalarPage(bool loggedIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/scalar/");

        if (loggedIn)
            request.Headers.Add("Cookie", LoginCookie(_app));

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A cookie the cookie scheme accepts, protected the way the Google login would have protected it.
    /// </summary>
    private static string LoginCookie(WebApplication app)
    {
        var options = app
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
