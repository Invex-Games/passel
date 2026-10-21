namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record DeletePacket
{
    public required string UserId { get; init; }

    public required Guid PacketId { get; init; }
}
