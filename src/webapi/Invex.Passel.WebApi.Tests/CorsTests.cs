using System.Net;
using Invex.Passel.WebApi.Extensions;
using Invex.Passel.WebApi.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Tests;

public sealed class CorsTests
{
    private const string WebOrigin = "https://web.test";

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    /// <summary>
    ///     A host with the API's CORS registration and middleware in front of one endpoint, configured with the given
    ///     settings.
    /// </summary>
    private static async Task<WebApplication> StartAsync(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        // The API's appsettings.json is copied next to the tests, and its AllowedHosts would answer 400 to the test
        // server's host
        builder.Configuration["AllowedHosts"] = "*";

        foreach (var (key, value) in settings)
            builder.Configuration[key] = value;

        builder.AddWebAppCors();

        var app = builder.Build();
        app.UseWebAppCors();
        app.MapGet("/api/packets", () => Results.Ok());

        await app.StartAsync(CancellationToken);
        return app;
    }

    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, "/api/packets")
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
                { "Access-Control-Request-Headers", "authorization" },
            },
        };

    [Theory]
    [InlineData("https://passel.link")]
    [InlineData("http://localhost:5245")]
    [InlineData("https://app.passel.link:8443")]
    public void IsOrigin_accepts_a_serialized_origin(string origin) =>
        Assert.True(WebAppCorsOptions.IsOrigin(origin));

    [Theory]
    [InlineData("https://passel.link/")]
    [InlineData("https://passel.link/app")]
    [InlineData("https://passel.link:443")]
    [InlineData("https://*.passel.link")]
    [InlineData("*")]
    [InlineData("passel.link")]
    [InlineData("ftp://passel.link")]
    [InlineData("https://user@passel.link")]
    [InlineData("")]
    public void IsOrigin_refuses_anything_a_browser_would_not_send_as_its_origin(string origin) =>
        Assert.False(WebAppCorsOptions.IsOrigin(origin));

    [Fact]
    public async Task A_misconfigured_origin_stops_the_host_from_starting()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartAsync(("Cors:AllowedOrigins:0", "https://passel.link/")));

        Assert.Contains("https://passel.link/", exception.Message);
    }

    [Fact]
    public async Task A_preflight_from_the_web_app_is_answered_with_what_it_may_send()
    {
        await using var app = await StartAsync(("Cors:AllowedOrigins:0", WebOrigin));
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(WebOrigin), CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WebOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("GET,POST,DELETE", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
        Assert.Contains("Authorization", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Headers")));
        Assert.Equal("600", Assert.Single(response.Headers.GetValues("Access-Control-Max-Age")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task A_request_from_the_web_app_may_read_the_response_and_its_retry_after()
    {
        await using var app = await StartAsync(("Cors:AllowedOrigins:0", WebOrigin));
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/packets") { Headers = { { "Origin", WebOrigin } } };
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("Retry-After", Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("https://web.test.evil.test")]
    [InlineData("http://web.test")]
    public async Task Any_other_origin_gets_no_CORS_headers(string origin)
    {
        await using var app = await StartAsync(("Cors:AllowedOrigins:0", WebOrigin));
        using var client = app.GetTestClient();

        using var preflight = await client.SendAsync(Preflight(origin), CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/packets") { Headers = { { "Origin", origin } } };
        using var response = await client.SendAsync(request, CancellationToken);

        // Both are answered, but nothing in the answers lets the browser hand them to the page
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Without_configuration_no_origin_is_allowed()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(WebOrigin), CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
