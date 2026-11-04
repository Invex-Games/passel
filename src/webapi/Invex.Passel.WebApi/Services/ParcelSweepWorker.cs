using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Runs the parcel sweep every <see cref="ParcelOptions.SweepInterval" />, and at once when a commit that deleted
///     a parcel pokes <see cref="ParcelSweepSignal" />, so a deleted file's bytes are gone within seconds and at worst
///     after one interval. Every <see cref="ParcelOptions.OrphanScanInterval" /> it also runs the orphan scan, the
///     first time one interval after the start. A sweep that fails is logged and the next one tries again.
/// </summary>
internal sealed partial class ParcelSweepWorker(
    IServiceScopeFactory scopeFactory,
    ParcelSweepSignal signal,
    IParcelStore store,
    IHostEnvironment environment,
    TimeProvider time,
    IOptions<ParcelOptions> options,
    ILogger<ParcelSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Said once, here, because this is the one part of the API that uses the storage whether or not anyone asks
        // for a file
        if (store is UnavailableParcelStore && !environment.IsDevelopment())
            LogStorageMissing(HostParcelExtensions.BlobConnectionName, HostParcelExtensions.StoreKey,
                HostParcelExtensions.FileSystemRootKey);

        var settings = options.Value;

        if (!settings.SweepEnabled)
        {
            LogDisabled();

            return;
        }

        using var timer = new PeriodicTimer(settings.SweepInterval, time);
        var lastOrphanScan = time.GetUtcNow();

        // Each wait is kept until it completes: a periodic timer allows only one wait at a time, and a poke that
        // arrives while the timer is waited on must not leave a second reader behind
        Task<bool>? tick = null;
        Task<bool>? poke = null;

        try
        {
            while (true)
            {
                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                poke ??= signal.Reader.WaitToReadAsync(stoppingToken).AsTask();

                var woken = await Task.WhenAny(tick, poke);

                if (!await woken)
                    return;

                if (woken == tick)
                    tick = null;
                else
                    poke = null;

                // Pokes that arrived meanwhile are answered by this sweep too
                while (signal.Reader.TryRead(out _))
                {
                }

                try
                {
                    await SweepAsync(stoppingToken);

                    if (time.GetUtcNow() - lastOrphanScan >= settings.OrphanScanInterval)
                    {
                        await ScanAsync(stoppingToken);
                        lastOrphanScan = time.GetUtcNow();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IParcelSweep>().RunOnceAsync(time.GetUtcNow(), cancellationToken);
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IParcelSweep>().ScanOrphansAsync(time.GetUtcNow(), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No file storage is configured, so files cannot be stored: every file request answers 503. Set the connection string {ConnectionName}, or {StoreKey}=FileSystem with {RootKey}")]
    private partial void LogStorageMissing(string connectionName, string storeKey, string rootKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "The parcel sweep is off; deleted and expired files keep their bytes until it is on")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "The parcel sweep failed; the next one tries again")]
    private partial void LogFailed(Exception exception);
}
