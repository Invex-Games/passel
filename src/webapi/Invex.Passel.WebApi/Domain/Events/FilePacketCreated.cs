namespace Invex.Passel.WebApi.Domain.Events;

[PublicAPI]
public sealed record FilePacketCreated
{
    public required string UserId { get; init; }

    public required string? FileName { get; init; }

    public required string ContentType { get; init; }

    public required bool IsImage { get; init; }

    public required Guid ParcelId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
