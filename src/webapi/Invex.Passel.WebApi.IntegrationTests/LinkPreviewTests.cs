using System.Net;
using System.Text.Json;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Link previews end to end: the host fetches the page of a link created through the API (from the tests' own
///     page server, reached through the factory's resolver and its loopback-allowing policy), stores what it found,
///     lists it and announces it as <c>packet-updated</c>.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class LinkPreviewTests(PasselApiFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     How long a stream is watched for an event that must not come. The page server answers in milliseconds
    ///     and a refused fetch never leaves the host, so a preview that was going to arrive has arrived by then.
    /// </summary>
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(500);

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private TestPageServer _pages = null!;

    public async ValueTask InitializeAsync() =>
        _pages = await TestPageServer.StartAsync(CancellationToken);

    public ValueTask DisposeAsync() =>
        _pages.DisposeAsync();

    private string PageUrl(string path) =>
        $"http://{PasselApiFactory.TestPagesHost}:{_pages.Port}{path}";

    [Fact]
    public async Task A_links_preview_arrives_as_a_packet_updated_event_and_on_the_list()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);
        var before = DateTimeOffset.UtcNow;

        var link = await alice.CreateLinkAsync("Article", PageUrl("/article"), CancellationToken);

        // The create answers before the page is fetched
        Assert.Null(link.Preview);

        var created = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, created.EventType);
        Assert.Equal(link, PacketJson.Parse(created.Data));

        var updated = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketUpdatedEventType, updated.EventType);

        // The same packet, with the preview the page described: the Open Graph tags, and the image made absolute
        var withPreview = PacketJson.Parse(updated.Data);

        Assert.NotNull(withPreview.Preview);
        Assert.Equal(link with { Preview = withPreview.Preview }, withPreview);
        Assert.Equal(TestPageServer.ArticleTitle, withPreview.Preview.Title);
        Assert.Equal(TestPageServer.ArticleDescription, withPreview.Preview.Description);
        Assert.Equal(PageUrl(TestPageServer.ArticleImagePath), withPreview.Preview.ImageUrl);
        Assert.InRange(withPreview.Preview.FetchedAt, before, DateTimeOffset.UtcNow);

        Assert.Equal([withPreview], (await alice.ListAsync(null, CancellationToken)).Packets);

        // The event has the committed fixture's shape, property for property, preview included
        var fixtures = ContractFixtures.Generate(fixture.Api.Services);
        using var sample = JsonDocument.Parse(fixtures[ContractFixtures.PacketUpdatedEvent]);
        using var data = JsonDocument.Parse(updated.Data);

        Assert.Equal(ContractTests.PropertyNames(sample.RootElement), ContractTests.PropertyNames(data.RootElement));
        Assert.Equal(ContractTests.PropertyNames(sample.RootElement.GetProperty("preview")),
            ContractTests.PropertyNames(data.RootElement.GetProperty("preview")));

        // One request, as Passel, to the host the link names: the connection went to the checked loopback address,
        // and the request still carried the name
        var request = Assert.Single(_pages.Requests);

        Assert.Equal("/article", request.Path);
        Assert.Equal($"{PasselApiFactory.TestPagesHost}:{_pages.Port}", request.Host);
        Assert.Equal(LinkPreviewFetcher.UserAgent, request.UserAgent);
    }

    [Fact]
    public async Task A_page_with_nothing_to_show_or_no_page_at_all_leaves_the_link_without_a_preview()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        // A page without a title, description or image; something that is not a page; and a host that resolves,
        // under the test resolver, to an address the fetcher may not reach, as a private host would
        await alice.CreateLinkAsync(null, PageUrl("/plain"), CancellationToken);
        await alice.CreateLinkAsync(null, PageUrl("/data.json"), CancellationToken);
        await alice.CreateLinkAsync(null, "https://example.com/", CancellationToken);

        for (var i = 0; i < 3; i++)
            Assert.Equal(EventStream.PacketCreatedEventType, (await stream.ReadAsync(CancellationToken)).EventType);

        // Nothing follows: no preview, and no event about there being none
        Assert.Null(await stream.TryReadAsync(Silence, CancellationToken));

        var page = await alice.ListAsync(null, CancellationToken);

        Assert.Equal(3, page.Packets.Count);
        Assert.All(page.Packets, packet => Assert.Null(packet.Preview));

        // The two pages were asked for; the refused link never reached any server
        Assert.Equal(["/data.json", "/plain"], _pages.Requests.Select(request => request.Path).Order());
    }

    [Fact]
    public async Task A_link_deleted_while_its_page_is_being_fetched_stays_deleted()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        var link = await alice.CreateLinkAsync("Slow", PageUrl("/slow"), CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, (await stream.ReadAsync(CancellationToken)).EventType);

        // The fetch is under way; the page server holds its answer back until the link is gone
        await _pages.SlowRequested.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(link.Id, CancellationToken));
        Assert.Equal(EventStream.PacketDeletedEventType, (await stream.ReadAsync(CancellationToken)).EventType);

        _pages.ReleaseSlow();

        // The preview finds no link to attach to: no update, and nothing comes back on the list
        Assert.Null(await stream.TryReadAsync(Silence, CancellationToken));
        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);
    }
}
