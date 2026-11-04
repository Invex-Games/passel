namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record RenameDevice
{
    public required string UserId { get; init; }

    public required Guid DeviceId { get; init; }

    public required string Name { get; init; }
}
