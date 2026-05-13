namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateLinkPacket
{
    public required string UserId { get; init; }

    public required string? Title { get; init; }

    public required string Url { get; init; }
}
