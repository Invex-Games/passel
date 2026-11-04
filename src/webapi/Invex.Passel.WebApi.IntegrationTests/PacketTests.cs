using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Creating, listing and deleting packets through the real API against a real database.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class PacketTests(PasselApiFixture fixture)
{
    private const int PageSize = 50;

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_without_a_user_are_refused()
    {
        using var client = fixture.Api.CreateClient();

        using var anonymous = await client.GetAsync(PacketApi.PacketsPath, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Dev", "not a valid id");
        using var badUser = await client.GetAsync(PacketApi.PacketsPath, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, badUser.StatusCode);
    }

    [Fact]
    public async Task A_created_packet_comes_back_as_sent_with_an_id_and_its_creation_time()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var before = DateTimeOffset.UtcNow;

        var text = await alice.CreateTextAsync("Shopping", "milk, eggs", CancellationToken);
        var link = await alice.CreateLinkAsync(null, "https://example.com/page?q=1", CancellationToken);

        Assert.Equal("Text", text.Type);
        Assert.Equal("Shopping", text.Title);
        Assert.Equal("milk, eggs", text.Body);
        Assert.InRange(text.CreatedAt, before, DateTimeOffset.UtcNow);

        Assert.Equal("Link", link.Type);
        Assert.Null(link.Name);
        Assert.Equal("https://example.com/page?q=1", link.Url);
        Assert.NotEqual(text.Id, link.Id);
    }

    [Fact]
    public async Task Each_user_lists_only_their_own_packets_newest_first()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        var note = await alice.CreateTextAsync("Note", "from alice", CancellationToken);
        await PacketApi.NextMillisecondAsync(CancellationToken);
        var link = await alice.CreateLinkAsync("Example", "https://example.com/", CancellationToken);
        var bobs = await bob.CreateTextAsync(null, "from bob", CancellationToken);

        var alicesPage = await alice.ListAsync(null, CancellationToken);
        var bobsPage = await bob.ListAsync(null, CancellationToken);

        // The listed packets are the created ones, field for field
        Assert.Equal([link, note], alicesPage.Packets);
        Assert.Null(alicesPage.NextCursor);

        Assert.Equal([bobs], bobsPage.Packets);
        Assert.Null(bobsPage.NextCursor);
    }

    [Fact]
    public async Task The_feed_pages_through_the_cursor_without_a_gap_or_an_overlap()
    {
        using var client = fixture.Api.CreateClientFor(PasselApiFixture.User("pager"));
        var created = new List<PacketJson>();

        for (var i = 1; i <= PageSize + 1; i++)
        {
            await PacketApi.NextMillisecondAsync(CancellationToken);
            created.Add(await client.CreateTextAsync(null, $"packet {i}", CancellationToken));
        }

        var newestFirst = created
            .AsEnumerable()
            .Reverse()
            .ToList();

        var firstPage = await client.ListAsync(null, CancellationToken);

        Assert.Equal(newestFirst.Take(PageSize), firstPage.Packets);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await client.ListAsync(firstPage.NextCursor, CancellationToken);

        Assert.Equal(newestFirst.Skip(PageSize), secondPage.Packets);
        Assert.Null(secondPage.NextCursor);

        Assert.Equal("packet 51", firstPage.Packets[0].Body);
        Assert.Equal("packet 1", Assert.Single(secondPage.Packets).Body);
    }

    [Fact]
    public async Task An_invalid_cursor_is_refused()
    {
        using var client = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        using var response = await client.GetAsync($"{PacketApi.PacketsPath}?cursor=not-a-cursor", CancellationToken);
        using var problem = await PacketApi.ReadJsonAsync(response, HttpStatusCode.BadRequest, CancellationToken);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_packet_the_API_cannot_read_is_refused_with_a_problem()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        // An unknown kind fails before validation; the suite runs in Development, which answers as Production does
        using var content = new StringContent("""{ "$type": "Video", "url": "https://example.com" }""",
            Encoding.UTF8,
            "application/json");
        using var response = await alice.PostAsync(PacketApi.PacketsPath, content, CancellationToken);
        using var problem = await PacketApi.ReadJsonAsync(response, HttpStatusCode.BadRequest, CancellationToken);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);
    }

    [Fact]
    public async Task A_packet_is_deleted_once_and_then_gone()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var packet = await alice.CreateTextAsync(null, "to delete", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(packet.Id, CancellationToken));
        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);

        // Deleting again finds nothing
        Assert.Equal(HttpStatusCode.NotFound, await alice.DeletePacketAsync(packet.Id, CancellationToken));
    }

    [Fact]
    public async Task Another_users_packet_cannot_be_deleted_nor_told_apart_from_a_missing_one()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));
        var bobs = await bob.CreateTextAsync(null, "from bob", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, await alice.DeletePacketAsync(bobs.Id, CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await alice.DeletePacketAsync("not-a-packet-id", CancellationToken));

        Assert.Equal([bobs], (await bob.ListAsync(null, CancellationToken)).Packets);
    }
}
