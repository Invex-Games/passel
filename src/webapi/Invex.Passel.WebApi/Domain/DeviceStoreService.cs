using Marten.Exceptions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     What a registration came to.
/// </summary>
[Union]
internal partial record RegisterDeviceResult
{
    /// <summary>
    ///     The installation was new: this is its device.
    /// </summary>
    public sealed partial record Registered(Device Device);

    /// <summary>
    ///     The installation was registered already: this is its device, seen just now.
    /// </summary>
    public sealed partial record Existing(Device Device);

    /// <summary>
    ///     The installation is registered with another platform.
    /// </summary>
    public sealed partial record PlatformMismatch(DevicePlatform RegisteredPlatform);

    /// <summary>
    ///     The installation is new and the user has as many devices as they may have.
    /// </summary>
    public sealed partial record LimitReached(QuotaExceeded Exceeded);
}

/// <summary>
///     What setting a push token came to.
/// </summary>
[Union]
internal partial record SetDevicePushResult
{
    public sealed partial record Stored;

    /// <summary>
    ///     The user has no such device.
    /// </summary>
    public sealed partial record NotFound;

    /// <summary>
    ///     The token is of a kind the device's platform does not use.
    /// </summary>
    public sealed partial record PlatformMismatch(DevicePlatform Platform);
}

internal interface IDeviceStoreService
{
    Task<RegisterDeviceResult> Handle(RegisterDevice command);

    /// <summary>
    ///     The renamed device, or <c>null</c> if the user has no such device.
    /// </summary>
    Task<Device?> Handle(RenameDevice command);

    /// <summary>
    ///     Returns <c>false</c> if the user has no such device.
    /// </summary>
    Task<bool> Handle(RemoveDevice command);

    Task<SetDevicePushResult> Handle(SetDevicePush command);

    /// <summary>
    ///     Returns <c>false</c> if the user has no such device; a device without a token is no error.
    /// </summary>
    Task<bool> Handle(ClearDevicePush command);
}

/// <summary>
///     Writes devices as documents. Every change to an existing device is a Marten update of the whole document,
///     which refuses one that is gone (so a device removed by a concurrent request stays removed) or that has changed
///     since it was read (optimistic concurrency, so one change never undoes another). Registering and setting a push
///     token can also race requests that write the same unique key. A change that loses a race reads again and
///     decides afresh; one that loses every time is a <see cref="DeviceWriteConflictException" />.
/// </summary>
internal sealed partial class DeviceStoreService(
    IDocumentSession session,
    IOptions<QuotaOptions> quotas,
    ILogger<DeviceStoreService> logger) : IDeviceStoreService
{
    /// <summary>
    ///     How often a write is decided before the request gives up. Registrations of one installation settle in two
    ///     attempts however many of them race, because the losers take the winner's device; the third covers a
    ///     rename, a push token change or a removal in between. Only several requests changing one device at the same
    ///     moment use up every attempt.
    /// </summary>
    private const int MaxAttempts = 3;

    public async Task<RegisterDeviceResult> Handle(RegisterDevice command)
    {
        // Once this request has lost a race: the LastSeenAt of the device it failed to update, or null when it
        // failed to insert one
        var lostRace = false;
        DateTimeOffset? lastSeenRead = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await FindAsync(command.UserId, command.InstallationId);

            if (existing is not null)
            {
                if (existing.Platform != command.Platform)
                    return new RegisterDeviceResult.PlatformMismatch(existing.Platform);

                // Lost to another registration of this installation, which has just registered the device or marked
                // it seen. Writing again would only race the other losers, so its device is the answer. A rename or a
                // push token change leaves LastSeenAt as it was, and losing to one of those writes again
                if (lostRace && existing.LastSeenAt != lastSeenRead)
                    return new RegisterDeviceResult.Existing(existing);

                // The name stays as it is: the user may have renamed the device since it first registered
                var seen = existing with
                {
                    LastSeenAt = now,
                };

                session.Update(seen);

                if (await TrySaveAsync(attempt))
                    return new RegisterDeviceResult.Existing(seen);

                // Changed since it was read, or removed, in which case registering again makes a new device
                (lostRace, lastSeenRead) = (true, existing.LastSeenAt);

                continue;
            }

            var devices = await session
                .Query<Device>()
                .CountAsync(d => d.UserId == command.UserId);

            if (DeviceQuota.Exceeded(devices, quotas.Value) is { } exceeded)
                return new RegisterDeviceResult.LimitReached(exceeded);

            var device = new Device
            {
                // Version 7, so the list orders devices by registration
                Id = Guid.CreateVersion7(),
                UserId = command.UserId,
                InstallationId = command.InstallationId,
                Name = command.Name,
                Platform = command.Platform,
                CreatedAt = now,
                LastSeenAt = now,
            };

            session.Insert(device);

            if (await TrySaveAsync(attempt))
                return new RegisterDeviceResult.Registered(device);

            // The same installation registered by a request that raced this one: its device is found next time round
            (lostRace, lastSeenRead) = (true, null);
        }

        // The last attempt throws rather than lose quietly
        throw new UnreachableException();
    }

    public async Task<Device?> Handle(RenameDevice command)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var device = await LoadOwnedAsync(command.UserId, command.DeviceId);

            if (device is null)
                return null;

            var renamed = device with
            {
                Name = command.Name,
            };

            session.Update(renamed);

            if (await TrySaveAsync(attempt))
                return renamed;
        }

        throw new UnreachableException();
    }

    public async Task<bool> Handle(RemoveDevice command)
    {
        var device = await LoadOwnedAsync(command.UserId, command.DeviceId);

        // Another user's device is reported as missing too, so device ids can't be probed
        if (device is null)
            return false;

        // Its push token goes with the document. The sessions of roadmap 1.B1 will be revoked here as well
        session.Delete(device);

        await session.SaveChangesAsync();

        return true;
    }

    public async Task<SetDevicePushResult> Handle(SetDevicePush command)
    {
        var key = DevicePush.KeyOf(command.Push);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var device = await LoadOwnedAsync(command.UserId, command.DeviceId);

            if (device is null)
                return new SetDevicePushResult.NotFound();

            if (!Accepts(device.Platform, command.Push))
                return new SetDevicePushResult.PlatformMismatch(device.Platform);

            // A token identifies an app installation or a browser profile, not an account: after a sign-out and a
            // sign-in as someone else, the same token arrives from the other account's device. It moves, so pushes
            // for the first account stop reaching a device that now belongs to the second
            var holders = await session
                .Query<Device>()
                .Where(d => d.PushKey == key && d.Id != device.Id)
                .ToListAsync();

            foreach (var holder in holders)
                session.Update(holder.WithPush(null));

            session.Update(device.WithPush(command.Push));

            // Lost a race: the device or a holder changed or was removed meanwhile, or another device took the token
            if (!await TrySaveAsync(attempt))
                continue;

            foreach (var holder in holders)
                LogPushMoved(holder.Id, device.Id);

            return new SetDevicePushResult.Stored();
        }

        throw new UnreachableException();
    }

    public async Task<bool> Handle(ClearDevicePush command)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var device = await LoadOwnedAsync(command.UserId, command.DeviceId);

            if (device is null)
                return false;

            if (device.Push is null)
                return true;

            session.Update(device.WithPush(null));

            if (await TrySaveAsync(attempt))
                return true;
        }

        throw new UnreachableException();
    }

    /// <summary>
    ///     Which kind of token a platform takes: an FCM token on Android, a Web Push subscription in a browser.
    /// </summary>
    internal static bool Accepts(DevicePlatform platform, DevicePush push) =>
        push switch
        {
            DevicePush.Fcm => platform is DevicePlatform.Android,
            DevicePush.WebPush => platform is DevicePlatform.Extension or DevicePlatform.Web,
            _ => false,
        };

    private Task<Device?> FindAsync(string userId, string installationId) =>
        session
            .Query<Device>()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.InstallationId == installationId);

    private async Task<Device?> LoadOwnedAsync(string userId, Guid deviceId)
    {
        var device = await session.LoadAsync<Device>(deviceId);

        return device?.UserId == userId
            ? device
            : null;
    }

    /// <summary>
    ///     Saves the pending changes. Returns <c>false</c>, with the changes dropped, when they lost a race: an update
    ///     of a document that is gone or has changed since it was read, or a write of a unique key another request has
    ///     just written. The caller reads again and decides afresh. A race lost on the last attempt is thrown as a
    ///     <see cref="DeviceWriteConflictException" />, which the API answers with 409.
    /// </summary>
    private async Task<bool> TrySaveAsync(int attempt)
    {
        try
        {
            await session.SaveChangesAsync();

            return true;
        }
        catch (Exception exception) when (IsLostRace(exception))
        {
            session.EjectAllPendingChanges();

            if (attempt < MaxAttempts)
                return false;

            throw new DeviceWriteConflictException(MaxAttempts, exception);
        }
    }

    private static bool IsLostRace(Exception exception)
    {
        // Marten may report several failed operations of one batch together
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.All(IsLostRace);

        for (var inner = exception; inner is not null; inner = inner.InnerException)
            if (inner is NonExistentDocumentException or ConcurrencyException
                or PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;

        return false;
    }

    // Device ids only: the token is never logged
    [LoggerMessage(Level = LogLevel.Information, Message = "Push token moved from device {FromDeviceId} to device {ToDeviceId}")]
    private partial void LogPushMoved(Guid fromDeviceId, Guid toDeviceId);
}
