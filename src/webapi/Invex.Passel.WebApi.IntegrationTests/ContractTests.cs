using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The committed contract files against the running API: the OpenAPI document it serves, the fixtures it
///     serializes, and the feed scenarios the client suites run. Each test says how to regenerate the file it
///     checks when it is out of date.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class ContractTests(PasselApiFixture fixture)
{
    private const string OpenApiPath = "/openapi/v1.json";

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    /// <summary>
    ///     The document is the one the Development host serves to Scalar, so it declares the Development scheme next
    ///     to the bearer token. Its <c>servers</c> entry is left out: it names the address this copy was served from,
    ///     which is the test host's.
    /// </summary>
    [Fact]
    public async Task The_committed_OpenAPI_document_is_the_one_the_API_serves()
    {
        // Served only in Development, on the localhost host, which is what the test client uses
        using var client = fixture.Api.CreateClient();
        using var response = await client.GetAsync(OpenApiPath, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!.AsObject();
        document.Remove("servers");

        await Contracts.AssertCurrentAsync(Contracts.OpenApiDocument,
            document.ToJsonString(DocumentWriterOptions),
            CancellationToken);
    }

    /// <summary>
    ///     Indented, with LF line endings, and without escaping the angle brackets and quotes the descriptions use.
    /// </summary>
    private static readonly JsonSerializerOptions DocumentWriterOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TheoryData<string> FixtureNames =>
        [..ContractFixtures.Names];

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task The_committed_fixture_is_what_the_API_serializes(string name)
    {
        var fixtures = ContractFixtures.Generate(fixture.Api.Services);

        await Contracts.AssertCurrentAsync($"{Contracts.FixturesDirectory}/{name}", fixtures[name], CancellationToken);
    }

    [Fact]
    public void No_fixture_is_committed_without_a_test_that_generates_it()
    {
        var committed = Directory
            .EnumerateFiles(Contracts.PathOf(Contracts.FixturesDirectory))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(ContractFixtures.Names.Order(), committed);
    }

    /// <summary>
    ///     The fixtures are serialized from the response types; this checks that the endpoints really answer with
    ///     those types, property for property, so a fixture can't be current while an endpoint returns something
    ///     else. File packets can't be created through the API, so only links and texts are compared.
    /// </summary>
    [Fact]
    public async Task The_endpoints_answer_in_the_shape_of_the_fixtures()
    {
        var fixtures = ContractFixtures.Generate(fixture.Api.Services);
        using var client = fixture.Api.CreateClientFor(PasselApiFixture.User("contracts"));

        await using var stream = await EventStream.OpenAsync(client, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        // The create-response fixture is a text, the packet-created fixture a link
        using var created = await PostAsync(client,
            new JsonObject
            {
                ["$type"] = "Text",
                ["title"] = "Shopping",
                ["body"] = "milk, eggs",
            });

        AssertSameProperties(fixtures[ContractFixtures.CreateResponse], created.RootElement, "the create response");

        var textCreated = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, textCreated.EventType);

        using var link = await PostAsync(client,
            new JsonObject
            {
                ["$type"] = "Link",
                ["name"] = null,
                ["url"] = "https://example.com/",
            });

        var linkCreated = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, linkCreated.EventType);

        using var createdData = JsonDocument.Parse(linkCreated.Data);
        AssertSameProperties(fixtures[ContractFixtures.PacketCreatedEvent], createdData.RootElement, "the packet-created event");

        using var listResponse = await client.GetAsync(PacketApi.PacketsPath, CancellationToken);
        using var list = await PacketApi.ReadJsonAsync(listResponse, HttpStatusCode.OK, CancellationToken);
        using var listFixture = JsonDocument.Parse(fixtures[ContractFixtures.ListPage]);

        Assert.Equal(PropertyNames(listFixture.RootElement), PropertyNames(list.RootElement));

        var listed = list.RootElement.GetProperty("packets").EnumerateArray().ToList();

        Assert.Equal(2, listed.Count);

        foreach (var packet in listed)
        {
            var type = packet.GetProperty("$type").GetString();

            var sample = listFixture
                .RootElement
                .GetProperty("packets")
                .EnumerateArray()
                .Single(candidate => candidate.GetProperty("$type").GetString() == type);

            Assert.Equal(PropertyNames(sample), PropertyNames(packet));
        }

        var id = created.RootElement.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, await client.DeletePacketAsync(id, CancellationToken));

        var deletedEvent = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketDeletedEventType, deletedEvent.EventType);

        using var deletedData = JsonDocument.Parse(deletedEvent.Data);
        AssertSameProperties(fixtures[ContractFixtures.PacketDeletedEvent], deletedData.RootElement, "the packet-deleted event");
    }

    /// <summary>
    ///     The scenario file is written by hand, so this keeps it on the wire contract: every packet in it must be
    ///     one the API could have sent, read with the API's own options, and every step must refer to packets the
    ///     file defines.
    /// </summary>
    [Fact]
    public async Task Every_packet_in_the_feed_scenarios_is_a_valid_wire_packet()
    {
        var options = ContractFixtures.SerializerOptions(fixture.Api.Services);

        using var document = JsonDocument.Parse(await Contracts.ReadAsync(Contracts.FeedScenarios, CancellationToken));
        var root = document.RootElement;
        var ids = new HashSet<string>();

        foreach (var entry in root.GetProperty("packets").EnumerateObject())
        {
            var packet = JsonSerializer.Deserialize<PacketEndpoints.PacketResponse>(entry.Value.GetRawText(), options);

            Assert.NotNull(packet);
            Assert.Equal(entry.Name, packet.Id);
            Assert.True(ids.Add(entry.Name));
        }

        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToList();

        Assert.NotEmpty(scenarios);

        foreach (var scenario in scenarios)
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            var where = $"{scenario.GetProperty("name").GetString()}, {step.GetProperty("action").GetString()}";

            if (step.TryGetProperty("serves", out var serves))
            {
                // A page is what GET /api/packets answers: the API's own page type must read it
                var page = JsonSerializer.Deserialize<PacketEndpoints.GetPacketsResponseBody>(
                    ToPage(serves, root.GetProperty("packets")),
                    options);

                Assert.NotNull(page);

                foreach (var id in serves.GetProperty("packets").EnumerateArray())
                    Assert.True(ids.Contains(id.GetString()!), $"{where}: serves an unknown packet {id}");
            }

            if (step.TryGetProperty("packet", out var created))
                Assert.True(ids.Contains(created.GetString()!), $"{where}: an unknown packet {created}");

            if (step.TryGetProperty("id", out var deleted))
                Assert.True(ids.Contains(deleted.GetString()!), $"{where}: an unknown packet {deleted}");

            foreach (var id in step.GetProperty("expect").GetProperty("packets").EnumerateArray())
                Assert.True(ids.Contains(id.GetString()!), $"{where}: expects an unknown packet {id}");
        }
    }

    /// <summary>
    ///     The page a step serves, as the list endpoint would send it: the packets looked up in the catalogue, and
    ///     the cursor that follows them.
    /// </summary>
    private static string ToPage(JsonElement serves, JsonElement catalogue)
    {
        var packets = new JsonArray();

        foreach (var id in serves.GetProperty("packets").EnumerateArray())
            packets.Add(JsonNode.Parse(catalogue.GetProperty(id.GetString()!).GetRawText()));

        return new JsonObject
        {
            ["packets"] = packets,
            ["nextCursor"] = JsonNode.Parse(serves.GetProperty("nextCursor").GetRawText()),
        }.ToJsonString();
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, JsonObject body)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(PacketApi.PacketsPath, content, CancellationToken);

        return await PacketApi.ReadJsonAsync(response, HttpStatusCode.OK, CancellationToken);
    }

    private static void AssertSameProperties(string fixtureJson, JsonElement actual, string what)
    {
        using var expected = JsonDocument.Parse(fixtureJson);

        Assert.True(PropertyNames(expected.RootElement).SequenceEqual(PropertyNames(actual)),
            $"{what} has the properties {string.Join(", ", PropertyNames(actual))}, the fixture " +
            $"{string.Join(", ", PropertyNames(expected.RootElement))}.");
    }

    internal static List<string> PropertyNames(JsonElement element) =>
        element
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToList();
}
