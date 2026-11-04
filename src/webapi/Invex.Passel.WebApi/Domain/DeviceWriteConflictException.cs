namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     A device write that lost a race on every attempt, because other requests kept changing the same device in
///     between. The request itself was sound and will most likely succeed when sent again, so
///     <see cref="DeviceWriteConflictHandler" /> answers it with 409 rather than 500.
/// </summary>
internal sealed class DeviceWriteConflictException(int attempts, Exception lastRace)
    : Exception($"A device write lost a race {attempts} times in a row.", lastRace);
