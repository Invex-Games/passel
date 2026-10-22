using System.Net;
using System.Text.Json;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The event stream end to end: a change committed through the API reaches the owner's stream by way of the
///     database notification and the listener.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class PacketEventTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_opens_with_a_resync_that_carries_the_retry_interval()
    {
        using var client = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        await using var stream = await EventStream.OpenAsync(client, CancellationToken);

        var first = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.ResyncEventType, first.EventType);
        Assert.Equal(string.Empty, first.Data);
        Assert.InRange(first.Retry ?? 0, 2000, 6000);
    }

    [Fact]
    public async Task Creates_and_deletes_reach_the_owners_stream_and_nobody_elses()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        await using var alicesStream = await EventStream.OpenAsync(alice, CancellationToken);
        await using var bobsStream = await EventStream.OpenAsync(bob, CancellationToken);
        await alicesStream.ReadAsync(CancellationToken);
        await bobsStream.ReadAsync(CancellationToken);

        // Bob's packet is announced before Alice's; events keep that order, so if it reached Alice it would come first
        var bobs = await bob.CreateTextAsync(null, "from bob", CancellationToken);
        var alices = await alice.CreateTextAsync("Alice", "from alice", CancellationToken);

        var created = await alicesStream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, created.EventType);
        Assert.Equal(alices, PacketJson.Parse(created.Data));

        var bobsCreated = await bobsStream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, bobsCreated.EventType);
        Assert.Equal(bobs, PacketJson.Parse(bobsCreated.Data));

        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(alices.Id, CancellationToken));

        var deleted = await alicesStream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketDeletedEventType, deleted.EventType);

        using var deletedData = JsonDocument.Parse(deleted.Data);
        Assert.Equal(alices.Id, deletedData.RootElement.GetProperty("id").GetString());

        // Nothing of Alice's reaches Bob
        Assert.Null(await bobsStream.TryReadAsync(TimeSpan.FromMilliseconds(500), CancellationToken));
    }
}
