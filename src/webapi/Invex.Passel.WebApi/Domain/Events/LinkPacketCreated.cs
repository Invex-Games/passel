namespace Invex.Passel.WebApi.Domain.Events;

[PublicAPI]
public sealed record LinkPacketCreated
{
    public required string UserId { get; init; }

    public required string? Title { get; init; }

    public required string Url { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
