namespace Invex.Passel.WebApi.Services;

[JsonConverter(typeof(JsonStringEnumConverter<PacketChange>))]
internal enum PacketChange
{
    Created,
    Deleted,

    /// <summary>
    ///     The packet's content changed after it was created: a link got its preview.
    /// </summary>
    Updated,
}

/// <summary>
///     The payload of a Postgres <c>NOTIFY</c> on <see cref="Channel" />, sent once a packet change is committed.
/// </summary>
internal sealed record PacketNotification(string UserId, Guid PacketId, PacketChange Change = PacketChange.Created)
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
