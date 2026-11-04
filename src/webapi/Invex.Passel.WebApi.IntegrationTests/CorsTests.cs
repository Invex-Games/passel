using System.Net;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The web app's cross-origin calls through the whole pipeline: a preflight carries no token, so it must be
///     answered ahead of authentication and rate limiting, and the answers to the real requests must let the page
///     read them.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class CorsTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static HttpRequestMessage Preflight(string path, string method, string headers, string origin) =>
        new(HttpMethod.Options, path)
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", method },
                { "Access-Control-Request-Headers", headers },
            },
        };

    [Theory]
    [InlineData(PacketApi.PacketsPath, "GET", "authorization")]
    [InlineData(PacketApi.PacketsPath, "POST", "authorization,content-type")]
    [InlineData(PacketApi.PacketsPath + "/some-id", "DELETE", "authorization")]
    [InlineData(PacketApi.EventsPath, "GET", "accept,authorization")]
    [InlineData(ParcelApi.ParcelsPath, "POST", "authorization,content-type")]
    [InlineData(ParcelApi.ParcelsPath + "/some-id", "GET", "authorization,if-none-match,range")]
    [InlineData(ParcelApi.ParcelsPath + "/some-id/thumbnail", "GET", "authorization,if-none-match")]
    [InlineData(ParcelApi.ParcelsPath + "/some-id", "DELETE", "authorization")]
    public async Task A_preflight_from_the_web_app_is_answered_without_a_token(string path, string method, string headers)
    {
        using var client = fixture.Api.CreateClient();

        using var response = await client.SendAsync(
            Preflight(path, method, headers, PasselApiFactory.WebAppOrigin), CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(PasselApiFactory.WebAppOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(method, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
    }

    [Fact]
    public async Task The_web_app_can_read_the_feed_it_asked_for_with_its_token()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        await alice.CreateTextAsync(null, "from the web", CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, PacketApi.PacketsPath)
        {
            Headers = { { "Origin", PasselApiFactory.WebAppOrigin } },
        };
        using var response = await alice.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PasselApiFactory.WebAppOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task The_web_app_can_read_what_it_needs_to_save_and_resume_a_file()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var parcel = await alice.UploadAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "notes.bin", "application/octet-stream",
            CancellationToken);

        using var response = await alice.DownloadParcelAsync(parcel.ParcelId,
            CancellationToken,
            request =>
            {
                request.Headers.Add("Origin", PasselApiFactory.WebAppOrigin);
                request.Headers.Range = new(2, 5);
            });

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(PasselApiFactory.WebAppOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));

        var exposed = Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers")).Split(',');

        Assert.Subset(exposed.ToHashSet(),
            new HashSet<string> { "ETag", "Content-Disposition", "Content-Range", "Accept-Ranges", "Content-Length" });
    }

    [Fact]
    public async Task Another_origin_is_not_allowed_to_read_anything()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        const string origin = "https://evil.test";

        using var preflight = await alice.SendAsync(
            Preflight(PacketApi.PacketsPath, "GET", "authorization", origin), CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, PacketApi.PacketsPath) { Headers = { { "Origin", origin } } };
        using var response = await alice.SendAsync(request, CancellationToken);

        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
