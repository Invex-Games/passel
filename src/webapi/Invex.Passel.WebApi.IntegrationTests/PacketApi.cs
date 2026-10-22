using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A link's preview as the API returns it, read straight from the JSON.
/// </summary>
internal sealed record PreviewJson(string? Title, string? Description, string? ImageUrl, DateTimeOffset FetchedAt)
{
    /// <summary>
    ///     The packet's preview, or <c>null</c> when it has none (or is not a link).
    /// </summary>
    public static PreviewJson? Parse(JsonElement packet) =>
        packet.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Object
            ? new(PacketJson.OptionalString(preview, "title"),
                PacketJson.OptionalString(preview, "description"),
                PacketJson.OptionalString(preview, "imageUrl"),
                preview.GetProperty("fetchedAt").GetDateTimeOffset())
            : null;
}

/// <summary>
///     A packet as the API returns it, read straight from the JSON: the discriminator, the id, the creation time,
///     whichever content fields the kind of packet has, and a link's preview once it has one.
/// </summary>
internal sealed record PacketJson(
    string Type,
    string Id,
    DateTimeOffset CreatedAt,
    string? Title,
    string? Body,
    string? Name,
    string? Url,
    PreviewJson? Preview)
{
    public static PacketJson Parse(JsonElement packet) =>
        new(packet.GetProperty("$type").GetString()!,
            packet.GetProperty("id").GetString()!,
            packet.GetProperty("createdAt").GetDateTimeOffset(),
            OptionalString(packet, "title"),
            OptionalString(packet, "body"),
            OptionalString(packet, "name"),
            OptionalString(packet, "url"),
            PreviewJson.Parse(packet));

    public static PacketJson Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return Parse(document.RootElement);
    }

    internal static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>
///     One page of <c>GET /api/packets</c>.
/// </summary>
internal sealed record PacketPage(IReadOnlyList<PacketJson> Packets, string? NextCursor);

/// <summary>
///     The packet endpoints, called the way a client calls them. Every call checks the status it expects and fails
///     with the response body when it gets another.
/// </summary>
internal static class PacketApi
{
    public const string PacketsPath = "/api/packets";
    public const string EventsPath = "/api/packets/events";

    public static Task<PacketJson> CreateTextAsync(
        this HttpClient client,
        string? title,
        string body,
        CancellationToken cancellationToken) =>
        CreateAsync(client,
            new JsonObject
            {
                ["$type"] = "Text",
                ["title"] = title,
                ["body"] = body,
            },
            cancellationToken);

    public static Task<PacketJson> CreateLinkAsync(
        this HttpClient client,
        string? name,
        string url,
        CancellationToken cancellationToken) =>
        CreateAsync(client,
            new JsonObject
            {
                ["$type"] = "Link",
                ["name"] = name,
                ["url"] = url,
            },
            cancellationToken);

    private static async Task<PacketJson> CreateAsync(
        HttpClient client,
        JsonObject packet,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(packet.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(PacketsPath, content, cancellationToken);
        using var document = await ReadJsonAsync(response, HttpStatusCode.OK, cancellationToken);

        return PacketJson.Parse(document.RootElement);
    }

    public static async Task<PacketPage> ListAsync(
        this HttpClient client,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var path = cursor is null
            ? PacketsPath
            : $"{PacketsPath}?cursor={Uri.EscapeDataString(cursor)}";

        using var response = await client.GetAsync(path, cancellationToken);
        using var document = await ReadJsonAsync(response, HttpStatusCode.OK, cancellationToken);

        var page = document.RootElement;

        var packets = page
            .GetProperty("packets")
            .EnumerateArray()
            .Select(packet => PacketJson.Parse(packet))
            .ToList();

        var nextCursor = page.GetProperty("nextCursor");

        return new(packets,
            nextCursor.ValueKind == JsonValueKind.Null
                ? null
                : nextCursor.GetString());
    }

    // Not DeleteAsync: HttpClient has one with this shape, and an instance method wins over an extension
    public static async Task<HttpStatusCode> DeletePacketAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync($"{PacketsPath}/{id}", cancellationToken);

        return response.StatusCode;
    }

    /// <summary>
    ///     The response body as JSON once the status is the expected one; any other status fails with the body.
    /// </summary>
    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var request = response.RequestMessage;

        Assert.True(response.StatusCode == expected,
            $"{request?.Method} {request?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode} {response.StatusCode} rather than {(int)expected} {expected}: {body}");

        return JsonDocument.Parse(body);
    }

    /// <summary>
    ///     Waits for the clock to reach the next millisecond. Packet ids are version 7 Guids, ordered by the
    ///     millisecond of their creation, so packets created within one millisecond list in no particular order; a
    ///     test that asserts the order of the feed creates its packets in distinct milliseconds.
    /// </summary>
    public static async Task NextMillisecondAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() == now)
            await Task.Delay(1, cancellationToken);
    }
}
