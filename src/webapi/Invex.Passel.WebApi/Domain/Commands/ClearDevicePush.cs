namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record ClearDevicePush
{
    public required string UserId { get; init; }

    public required Guid DeviceId { get; init; }
}
