namespace Invex.Passel.WebApi.Domain.Commands;

/// <summary>
///     Registers the installation as one of the user's devices, or finds the device it already is.
/// </summary>
[PublicAPI]
public sealed record RegisterDevice
{
    public required string UserId { get; init; }

    public required string InstallationId { get; init; }

    /// <summary>
    ///     The name for a new device. A device that already exists keeps its own, which the user may have changed.
    /// </summary>
    public required string Name { get; init; }

    public required DevicePlatform Platform { get; init; }
}
