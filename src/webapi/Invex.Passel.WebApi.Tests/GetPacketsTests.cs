using System.Text.Json;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Configuration;

namespace Invex.Passel.WebApi.Tests;

public sealed class GetPacketsTests
{
    [Fact]
    public void ToResponsePacket_carries_the_public_id_and_the_creation_time()
    {
        var encoder = new IdEncodingService(new ConfigurationBuilder().Build());
        var createdAt = new DateTimeOffset(2026, 10, 22, 11, 3, 8, TimeSpan.Zero);

        var packet = new Packet
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            CreatedAt = createdAt,
            Content = new PacketContent.Link
            {
                Name = "Example",
                Url = "https://example.com",
            },
        };

        var response = PacketEndpoints.ToResponsePacket(packet, encoder);

        var link = Assert.IsType<PacketEndpoints.GetPacketsResponseBody.Packet.Link>(response);
        Assert.Equal(encoder.Encode(packet.Id), link.Id);
        Assert.Equal(createdAt, link.CreatedAt);
        Assert.Equal("https://example.com", link.Url);

        // The wire shape, as the API serializes it
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"$type\":\"Link\"", json);
        Assert.Contains("\"createdAt\":\"2026-10-22T11:03:08+00:00\"", json);
    }

    private static List<Packet> CreatePackets(int count) =>
        Enumerable
            .Range(0, count)
            .Select(i => new Packet
            {
                Id = Guid.CreateVersion7(),
                UserId = "user",
                CreatedAt = DateTimeOffset.UtcNow,
                Content = new PacketContent.Text
                {
                    Title = null,
                    Body = $"packet {i}",
                },
            })
            .ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void Paginate_returns_everything_without_cursor_when_no_more_rows(int count)
    {
        var fetched = CreatePackets(count);

        var (page, nextCursor) = PacketEndpoints.Paginate(fetched, 5);

        Assert.Equal(fetched, page);
        Assert.Null(nextCursor);
    }

    [Fact]
    public void Paginate_uses_last_returned_row_as_cursor_when_more_rows_exist()
    {
        var fetched = CreatePackets(6);

        var (page, nextCursor) = PacketEndpoints.Paginate(fetched, 5);

        Assert.Equal(fetched.Take(5), page);

        // The extra (6th) row must not become the cursor, or it would be skipped by the next "Id < cursor" query
        Assert.Equal(fetched[4].Id, nextCursor);
    }
}
