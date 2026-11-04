using System.Collections.Concurrent;
using System.Threading.Channels;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     When the worker runs the sweep and the orphan scan, against a sweep that only records its calls. The interval
///     is an hour, so in these tests only a poke wakes it, and the clock is moved by hand to bring the orphan scan due.
/// </summary>
public sealed class ParcelSweepWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() =>
            Now;
    }

    /// <summary>
    ///     Records each call as what was called and the time it was given; fails the sweep while
    ///     <see cref="Failing" /> is set, and holds each sweep until the test releases <see cref="Gate" /> when
    ///     one is set.
    /// </summary>
    private sealed class RecordingSweep : IParcelSweep
    {
        public Channel<(string Call, DateTimeOffset Now)> Calls { get; } = Channel.CreateUnbounded<(string, DateTimeOffset)>();

        public bool Failing { get; set; }

        public SemaphoreSlim? Gate { get; set; }

        public async Task<ParcelSweepResult> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Writer.TryWrite(("sweep", now));

            if (Gate is not null)
                await Gate.WaitAsync(cancellationToken);

            return Failing
                ? throw new InvalidOperationException("The sweep failed.")
                : new ParcelSweepResult(0, 0, 0);
        }

        public Task<ParcelOrphanResult> ScanOrphansAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Writer.TryWrite(("scan", now));

            return Task.FromResult(new ParcelOrphanResult(0, 0));
        }

        public async Task<(string Call, DateTimeOffset Now)> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(Timeout);

            return await Calls.Reader.ReadAsync(timeout.Token);
        }
    }

    private sealed class RecordingLogger : ILogger<ParcelSweepWorker>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed record Worker(
        ParcelSweepWorker Service,
        ParcelSweepSignal Signal,
        RecordingSweep Sweep,
        Clock Clock,
        RecordingLogger Logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }

    private static async Task<Worker> StartAsync(
        bool sweepEnabled = true,
        string environmentName = "Production",
        IParcelStore? store = null)
    {
        var sweep = new RecordingSweep();
        var services = new ServiceCollection()
            .AddScoped<IParcelSweep>(_ => sweep)
            .BuildServiceProvider();

        var signal = new ParcelSweepSignal();
        var clock = new Clock();
        var logger = new RecordingLogger();

        var worker = new ParcelSweepWorker(services.GetRequiredService<IServiceScopeFactory>(),
            signal,
            store ?? new UnavailableParcelStore(),
            new HostingEnvironment { EnvironmentName = environmentName },
            clock,
            Microsoft.Extensions.Options.Options.Create(new ParcelOptions
            {
                SweepEnabled = sweepEnabled,
                SweepInterval = TimeSpan.FromHours(1),
                OrphanScanInterval = TimeSpan.FromDays(1),
            }),
            logger);

        await worker.StartAsync(CancellationToken);

        return new(worker, signal, sweep, clock, logger);
    }

    [Fact]
    public async Task A_poke_sweeps_at_once_as_of_the_time_it_is_then()
    {
        await using var worker = await StartAsync();

        worker.Signal.Poke();

        Assert.Equal(("sweep", worker.Clock.Now), await worker.Sweep.NextAsync());
    }

    [Fact]
    public async Task The_orphan_scan_runs_after_a_sweep_once_its_interval_has_passed()
    {
        await using var worker = await StartAsync();
        using var gate = new SemaphoreSlim(0);
        worker.Sweep.Gate = gate;
        var started = worker.Clock.Now;

        // Each sweep is held while the clock is set for the check that follows it; the next poke's sweep arriving
        // shows that the check before it is done
        worker.Signal.Poke();

        Assert.Equal(("sweep", started), await worker.Sweep.NextAsync());

        gate.Release();
        worker.Signal.Poke();

        Assert.Equal(("sweep", started), await worker.Sweep.NextAsync());

        var due = started + TimeSpan.FromDays(1);
        worker.Clock.Now = due;
        gate.Release();

        Assert.Equal(("scan", due), await worker.Sweep.NextAsync());

        // Not again until another interval has passed since that scan
        worker.Signal.Poke();

        Assert.Equal(("sweep", due), await worker.Sweep.NextAsync());

        worker.Clock.Now = due + TimeSpan.FromHours(23);
        gate.Release();
        worker.Signal.Poke();

        Assert.Equal(("sweep", worker.Clock.Now), await worker.Sweep.NextAsync());

        worker.Sweep.Gate = null;
        gate.Release();
    }

    [Fact]
    public async Task A_failed_sweep_is_logged_and_the_next_poke_sweeps_again()
    {
        await using var worker = await StartAsync();
        worker.Sweep.Failing = true;

        worker.Signal.Poke();

        Assert.Equal("sweep", (await worker.Sweep.NextAsync()).Call);

        worker.Sweep.Failing = false;
        worker.Signal.Poke();

        Assert.Equal("sweep", (await worker.Sweep.NextAsync()).Call);
        Assert.Contains(worker.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.False(worker.Service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task A_sweep_that_is_off_never_runs()
    {
        await using var worker = await StartAsync(sweepEnabled: false);

        // Done as soon as it starts: nothing is left that a poke could wake
        await worker.Service.ExecuteTask!.WaitAsync(Timeout, CancellationToken);

        Assert.True(worker.Service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task Missing_storage_is_reported_once_at_the_start_outside_development(
        string environmentName,
        bool reported)
    {
        await using var worker = await StartAsync(sweepEnabled: false, environmentName: environmentName);
        await worker.Service.ExecuteTask!.WaitAsync(Timeout, CancellationToken);

        var warnings = worker.Logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

        if (reported)
            Assert.Contains("passel-blobs", Assert.Single(warnings).Message);
        else
            Assert.Empty(warnings);
    }

    [Fact]
    public async Task Storage_that_is_set_up_is_not_reported()
    {
        await using var worker = await StartAsync(sweepEnabled: false, store: new SilentStore());
        await worker.Service.ExecuteTask!.WaitAsync(Timeout, CancellationToken);

        Assert.DoesNotContain(worker.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    ///     Any store but the unavailable one; never called.
    /// </summary>
    private sealed class SilentStore : IParcelStore
    {
        public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ParcelContent?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StoredBlob> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
