using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.PacketEndpoints;

namespace Invex.Passel.WebApi.Tests;

public sealed class GetPacketsTests
{
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
