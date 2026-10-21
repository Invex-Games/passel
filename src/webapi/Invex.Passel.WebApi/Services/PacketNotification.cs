namespace Invex.Passel.WebApi.Services;

/// <summary>
///     The payload of a Postgres <c>NOTIFY</c> on <see cref="Channel" />, sent once a created packet is committed.
/// </summary>
internal sealed record PacketNotification(string UserId, Guid PacketId)
{
    public const string Channel = "passel_packet_events";

    public string ToPayload() =>
        JsonSerializer.Serialize(this);

    public static bool TryParse(string? payload, [NotNullWhen(true)] out PacketNotification? notification)
    {
        notification = null;

        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            notification = JsonSerializer.Deserialize<PacketNotification>(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (notification is { UserId.Length: > 0 })
            return true;

        notification = null;

        return false;
    }
}
