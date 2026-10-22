namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateTextPacket
{
    public required string UserId { get; init; }

    public required string? Title { get; init; }

    public required string Body { get; init; }
}
