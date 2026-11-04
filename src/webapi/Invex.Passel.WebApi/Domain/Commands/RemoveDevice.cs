namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record RemoveDevice
{
    public required string UserId { get; init; }

    public required Guid DeviceId { get; init; }
}
