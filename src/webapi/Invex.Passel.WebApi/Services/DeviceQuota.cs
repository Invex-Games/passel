using System.Globalization;

namespace Invex.Passel.WebApi.Services;

internal static class DeviceQuota
{
    /// <summary>
    ///     Decides whether a user with <paramref name="devices" /> devices may register another: <c>null</c> when they
    ///     may, otherwise the refusal. Checked only for an installation that is not registered yet, so a device that
    ///     registers again is never refused. Nothing ties the count to the insert, so new installations registering at
    ///     the same moment all pass against the same count: a burst can pass the cap by as many registrations as the
    ///     device-writes rate limit admits at once: <c>RateLimits:DeviceWritesPerMinute</c> less one, on each API
    ///     instance.
    /// </summary>
    internal static QuotaExceeded? Exceeded(long devices, QuotaOptions quotas) =>
        devices >= quotas.MaxDevicesPerUser
            ? new(StatusCodes.Status429TooManyRequests,
                "Device limit reached",
                $"You have {devices.ToString("N0", CultureInfo.InvariantCulture)} of the " +
                $"{quotas.MaxDevicesPerUser.ToString("N0", CultureInfo.InvariantCulture)} devices allowed. Remove one " +
                "you no longer use to add this one.")
            : null;
}
