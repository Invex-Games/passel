using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The API as <c>Program.cs</c> builds it, in the Development environment, against the given database. Clients
///     from <see cref="CreateClientFor" /> are signed in through the Development <c>Dev</c> scheme.
/// </summary>
internal sealed class PasselApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>
    ///     How long the live-update path may take to open once the host has started.
    /// </summary>
    private static readonly TimeSpan LiveUpdatesTimeout = TimeSpan.FromSeconds(30);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Settings given here reach Program.cs as command-line arguments, so they win over every settings file,
        // including the git-ignored appsettings.Development.json next to the API, which may carry a developer's
        // Google settings or connection string
        builder.UseSetting("ConnectionStrings:passel-db", connectionString);
        builder.UseSetting("Authentication:Google:ClientId", string.Empty);
        builder.UseSetting("Authentication:Google:ClientSecret", string.Empty);

        // The suite makes more requests per user in a minute than the defaults allow (paging alone creates 51
        // packets); the limits have unit tests of their own
        builder.UseSetting("RateLimits:CreatesPerMinute", "10000");
        builder.UseSetting("RateLimits:ReadsPerMinute", "10000");
        builder.UseSetting("RateLimits:StreamsPerMinute", "10000");

        // Only problems reach the test output
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    /// <summary>
    ///     A client whose requests are signed in as <paramref name="userId" />.
    /// </summary>
    public HttpClient CreateClientFor(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Dev", userId);

        return client;
    }

    /// <summary>
    ///     Starts the host and waits until a packet created through the API arrives on an event stream. The listener
    ///     that turns database notifications into events connects after the host has started, so a packet created
    ///     before it listens is announced to nobody; the tests must not start until the path is open.
    /// </summary>
    public async Task WaitUntilLiveUpdatesFlowAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClientFor(PasselApiFixture.User("probe"));
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            // A stream per attempt: an attempt whose packet was lost leaves nothing behind to confuse the next one
            await using var stream = await EventStream.OpenAsync(client, cancellationToken);
            await stream.ReadAsync(cancellationToken);

            await client.CreateTextAsync(null, "probe", cancellationToken);

            var arrived = await stream.TryReadAsync(TimeSpan.FromMilliseconds(500), cancellationToken);

            if (arrived is { EventType: EventStream.PacketCreatedEventType })
                return;

            if (Stopwatch.GetElapsedTime(started) > LiveUpdatesTimeout)
                throw new TimeoutException(
                    $"No packet event arrived on a stream within {LiveUpdatesTimeout}; live updates are not flowing.");
        }
    }
}
