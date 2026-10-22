using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class PacketNotificationTests
{
    [Fact]
    public void Payload_round_trips()
    {
        var notification = new PacketNotification("user", Guid.CreateVersion7());

        Assert.True(PacketNotification.TryParse(notification.ToPayload(), out var parsed));
        Assert.Equal(notification, parsed);
    }

    [Fact]
    public void Deletions_round_trip()
    {
        var notification = new PacketNotification("user", Guid.CreateVersion7(), PacketChange.Deleted);

        Assert.Contains("\"Deleted\"", notification.ToPayload());
        Assert.True(PacketNotification.TryParse(notification.ToPayload(), out var parsed));
        Assert.Equal(PacketChange.Deleted, parsed.Change);
    }

    [Fact]
    public void Updates_round_trip()
    {
        var notification = new PacketNotification("user", Guid.CreateVersion7(), PacketChange.Updated);

        Assert.Contains("\"Updated\"", notification.ToPayload());
        Assert.True(PacketNotification.TryParse(notification.ToPayload(), out var parsed));
        Assert.Equal(PacketChange.Updated, parsed.Change);
    }

    [Fact]
    public void A_payload_without_a_change_is_a_creation()
    {
        // As sent before deletions existed
        const string payload = """{"UserId":"user","PacketId":"0198f1c2-0000-7000-8000-000000000000"}""";

        Assert.True(PacketNotification.TryParse(payload, out var parsed));
        Assert.Equal(PacketChange.Created, parsed.Change);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"UserId":"","PacketId":"0198f1c2-0000-7000-8000-000000000000"}""")]
    public void TryParse_rejects_invalid_payloads(string? payload)
    {
        Assert.False(PacketNotification.TryParse(payload, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Payload_fits_in_a_postgres_notification()
    {
        // NOTIFY payloads must be shorter than 8000 bytes
        var payload = new PacketNotification(new('1', 255), Guid.CreateVersion7()).ToPayload();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) < 8000);
    }
}
