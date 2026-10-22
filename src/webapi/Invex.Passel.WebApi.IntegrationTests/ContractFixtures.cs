using System.Text.Json;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The sample payloads under <c>contracts/fixtures/</c>: fixed packets, mapped and serialized exactly as the API
///     does it (the one mapper, the JSON options the host is configured with), so the files are the same on every
///     machine and the client suites can parse them with their real parsers.
/// </summary>
internal static class ContractFixtures
{
    public const string ListPage = "list-page.json";
    public const string CreateResponse = "create-response.json";
    public const string PacketCreatedEvent = "packet-created-event.json";
    public const string PacketDeletedEvent = "packet-deleted-event.json";

    public static readonly string[] Names = [ListPage, CreateResponse, PacketCreatedEvent, PacketDeletedEvent];

    /// <summary>
    ///     A page smaller than the real one, so the list fixture shows a cursor without fifty packets.
    /// </summary>
    private const int PageSize = 3;

    private const string UserId = "fixtures";

    /// <summary>
    ///     The documentation's example timestamp, with a fraction so the format shows all seven digits.
    /// </summary>
    private static readonly DateTimeOffset Newest = new DateTimeOffset(2026, 10, 22, 11, 3, 8, TimeSpan.Zero).AddTicks(1234567);

    /// <summary>
    ///     Four stored packets, newest first as the feed lists them: one of each kind, and an older one that only
    ///     makes the page end with a cursor.
    /// </summary>
    private static readonly Packet[] Stored =
    [
        new()
        {
            Id = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0a04"),
            UserId = UserId,
            CreatedAt = Newest,
            Content = new PacketContent.Link
            {
                Name = "Example",
                Url = "https://example.com/page?q=1",
            },
        },
        new()
        {
            Id = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0a03"),
            UserId = UserId,
            CreatedAt = Newest.AddMinutes(-1),
            Content = new PacketContent.Text
            {
                Title = null,
                Body = "Hello from my laptop\nSecond line, with a café",
            },
        },
        new()
        {
            Id = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0a02"),
            UserId = UserId,
            CreatedAt = Newest.AddMinutes(-2),
            Content = new PacketContent.File
            {
                FileName = "screenshot.png",
                ContentType = "image/png",
                ParcelId = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0b01"),
                IsImage = true,
            },
        },
        new()
        {
            Id = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0a01"),
            UserId = UserId,
            CreatedAt = Newest.AddMinutes(-3),
            Content = new PacketContent.Text
            {
                Title = "Shopping",
                Body = "milk, eggs",
            },
        },
    ];

    /// <summary>
    ///     The JSON options every endpoint serializes with, resolved from the running host, plus indentation and LF
    ///     line endings so the files read and diff well. Neither changes what the clients receive.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions(IServiceProvider services) =>
        new(services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions)
        {
            WriteIndented = true,
            NewLine = "\n",
        };

    /// <summary>
    ///     Every fixture by file name, serialized as the API serializes it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Generate(IServiceProvider services)
    {
        var encodingService = services.GetRequiredService<IIdEncodingService>();
        var options = SerializerOptions(services);

        var (page, nextCursor) = PacketEndpoints.Paginate(Stored, PageSize);

        var listPage = new PacketEndpoints.GetPacketsResponseBody
        {
            Packets = page
                .Select(packet => PacketEndpoints.PacketResponse.From(packet, encodingService))
                .ToList(),
            NextCursor = nextCursor is { } next
                ? encodingService.Encode(next)
                : null,
        };

        // Serialized as the union, the way the endpoints return it, so the discriminator is written
        var createResponse = PacketEndpoints.PacketResponse.From(Stored[1], encodingService);
        var createdEvent = PacketEndpoints.PacketResponse.From(Stored[0], encodingService);

        var deletedEvent = new PacketEndpoints.PacketDeletedEventData
        {
            Id = encodingService.Encode(Stored[3].Id),
        };

        return new Dictionary<string, string>
        {
            [ListPage] = JsonSerializer.Serialize(listPage, options),
            [CreateResponse] = JsonSerializer.Serialize(createResponse, options),
            [PacketCreatedEvent] = JsonSerializer.Serialize(createdEvent, options),
            [PacketDeletedEvent] = JsonSerializer.Serialize(deletedEvent, options),
        };
    }
}
