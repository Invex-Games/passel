using System.Text.Json;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Configuration;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The mapping from a stored packet to its wire shape. What the wire shape serializes to is checked against the
///     committed fixtures by the integration tests, with the API's real JSON options.
/// </summary>
public sealed class PacketResponseTests
{
    private static readonly IdEncodingService Encoder = new(new ConfigurationBuilder().Build());

    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 22, 11, 3, 8, TimeSpan.Zero);

    private static Packet Stored(PacketContent content) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            CreatedAt = CreatedAt,
            Content = content,
        };

    [Fact]
    public void A_link_carries_its_public_id_its_creation_time_and_its_content()
    {
        var packet = Stored(new PacketContent.Link
        {
            Name = "Example",
            Url = "https://example.com",
        });

        var link = Assert.IsType<PacketEndpoints.PacketResponse.Link>(PacketEndpoints.PacketResponse.From(packet, Encoder));

        Assert.Equal(Encoder.Encode(packet.Id), link.Id);
        Assert.Equal(CreatedAt, link.CreatedAt);
        Assert.Equal("Example", link.Name);
        Assert.Equal("https://example.com", link.Url);
    }

    [Fact]
    public void A_text_carries_its_title_and_body()
    {
        var packet = Stored(new PacketContent.Text
        {
            Title = null,
            Body = "Hello",
        });

        var text = Assert.IsType<PacketEndpoints.PacketResponse.Text>(PacketEndpoints.PacketResponse.From(packet, Encoder));

        Assert.Equal(Encoder.Encode(packet.Id), text.Id);
        Assert.Null(text.Title);
        Assert.Equal("Hello", text.Body);
    }

    [Fact]
    public void A_file_carries_its_parcel_as_a_public_id()
    {
        var parcelId = Guid.CreateVersion7();

        var packet = Stored(new PacketContent.File
        {
            FileName = "screenshot.png",
            ContentType = "image/png",
            ParcelId = parcelId,
            IsImage = true,
        });

        var file = Assert.IsType<PacketEndpoints.PacketResponse.File>(PacketEndpoints.PacketResponse.From(packet, Encoder));

        Assert.Equal(Encoder.Encode(parcelId), file.ParcelId);
        Assert.Equal("screenshot.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.True(file.IsImage);
    }

    [Fact]
    public void The_wire_shape_leads_with_the_discriminator_the_id_and_the_creation_time()
    {
        var packet = Stored(new PacketContent.Link
        {
            Name = "Example",
            Url = "https://example.com",
        });

        // Serialized as the union, the way every endpoint returns it, so the discriminator is written
        var json = JsonSerializer.Serialize(PacketEndpoints.PacketResponse.From(packet, Encoder),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.StartsWith($"{{\"$type\":\"Link\",\"id\":\"{Encoder.Encode(packet.Id)}\",\"createdAt\":\"2026-10-22T11:03:08+00:00\",", json);
        Assert.EndsWith("\"name\":\"Example\",\"url\":\"https://example.com\"}", json);
    }
}
