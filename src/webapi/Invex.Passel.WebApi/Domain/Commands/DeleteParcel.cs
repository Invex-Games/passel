namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record DeleteParcel
{
    public required string UserId { get; init; }

    public required Guid ParcelId { get; init; }
}
