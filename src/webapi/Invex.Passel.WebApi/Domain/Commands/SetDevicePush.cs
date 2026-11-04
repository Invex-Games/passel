namespace Invex.Passel.WebApi.Domain.Commands;

/// <summary>
///     Gives the device its push token, replacing any it had. The token leaves whichever other device held it.
/// </summary>
[PublicAPI]
public sealed record SetDevicePush
{
    public required string UserId { get; init; }

    public required Guid DeviceId { get; init; }

    public required DevicePush Push { get; init; }
}
