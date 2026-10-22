namespace Invex.Passel.WebApi.Domain.Events;

[PublicAPI]
public sealed record TextPacketCreated
{
    public required string UserId { get; init; }

    public required string? Title { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
