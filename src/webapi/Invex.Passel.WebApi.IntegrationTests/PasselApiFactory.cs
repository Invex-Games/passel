using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Invex.Passel.WebApi.Services;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The API as <c>Program.cs</c> builds it, in the Development environment, against the given database. Clients
///     from <see cref="CreateClientFor" /> are signed in through the Development <c>Dev</c> scheme. With
///     <paramref name="migratedSchema" />, the API treats the schema as every other environment does: it applies
///     nothing and checks at startup that the schema has what it needs, so it can run on a role that has only DML.
///     Files go to the blob storage at <paramref name="blobConnectionString" />, or, without one, to an
///     <see cref="InMemoryParcelStore" />. <paramref name="settings" /> are applied last, over the factory's own.
/// </summary>
internal sealed class PasselApiFactory(
    string connectionString,
    bool migratedSchema = false,
    string? blobConnectionString = null,
    IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    /// <summary>
    ///     The host name the tests link their pages by. The factory's resolver points it at the loopback, where
    ///     <see cref="TestPageServer" /> listens, and the factory's address policy lets the fetcher go there.
    /// </summary>
    public const string TestPagesHost = "pages.test";

    /// <summary>
    ///     The one origin the API allows across origins in the tests.
    /// </summary>
    public const string WebAppOrigin = "https://web.test";

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

        if (blobConnectionString is not null)
            builder.UseSetting("ConnectionStrings:passel-blobs", blobConnectionString);
        builder.UseSetting("Authentication:Google:ClientId", string.Empty);
        builder.UseSetting("Authentication:Google:ClientSecret", string.Empty);

        // The contract fixtures carry ids encoded by the API; pinned so they come out the same on every machine
        builder.UseSetting("Encoding:Alphabet", IdEncodingService.DefaultAlphabet);

        // The suite makes more requests per user in a minute than the defaults allow (paging alone creates 51
        // packets, and the device cap test registers a user's whole allowance); the limits have unit tests of their own
        builder.UseSetting("RateLimits:CreatesPerMinute", "10000");
        builder.UseSetting("RateLimits:ReadsPerMinute", "10000");
        builder.UseSetting("RateLimits:StreamsPerMinute", "10000");
        builder.UseSetting("RateLimits:DeviceWritesPerMinute", "10000");
        builder.UseSetting("RateLimits:ParcelUploadsPerMinute", "10000");
        builder.UseSetting("RateLimits:ParcelReadsPerMinute", "10000");

        // Files small enough to make the limits cheap to reach: a 1 MiB file limit, 3 MiB of files per user and five
        // unsent files. The sweep never runs on its own; the tests that need it run it at a time of their choosing
        builder.UseSetting("Parcels:SweepEnabled", "false");
        builder.UseSetting("Parcels:MaxParcelBytes", "1048576");
        builder.UseSetting("Quotas:MaxParcelBytesPerUser", "3145728");
        builder.UseSetting("Quotas:MaxPendingParcelsPerUser", "5");

        // The web app's origin, so the CORS tests see the policy as a deployment with a web app configures it
        builder.UseSetting("Cors:AllowedOrigins:0", WebAppOrigin);

        // Only problems reach the test output
        builder.UseSetting("Logging:LogLevel:Default", "Warning");

        // The test server has no request size limit to apply, and warns so on every request to an endpoint that
        // declares one, which every create and device write does
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware", "Error");

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);

        // The link preview fetcher must never leave the test machine. Names are resolved by a stub rather than DNS:
        // the page server's name to the loopback, which the policy here allows, and every other name (the links to
        // example.com the other tests create) to a private address, which it refuses as the real policy would. So
        // nothing is fetched from the internet, and a refused fetch is over before the test that created the link
        // goes on. The fetcher takes the resolver as an optional constructor parameter, which the container fills
        // in once the delegate type is registered
        builder.ConfigureTestServices(services =>
        {
            if (migratedSchema)
            {
                services.ConfigureMarten(options =>
                {
                    options.AutoCreateSchemaObjects = AutoCreate.None;
                    SetStartupFlag(options, "ShouldApplyChangesOnStartup", false);
                });

                services.AddHostedService<DatabaseSchemaCheck>();
            }

            // Hosts started beside the shared one have no blob storage of their own
            if (blobConnectionString is null)
                services.Replace(ServiceDescriptor.Singleton<IParcelStore>(new InMemoryParcelStore()));

            services.AddSingleton<ILinkPreviewAddressPolicy, LoopbackAllowingAddressPolicy>();

            LinkPreviewFetcher.HostResolver resolver = ResolveOfflineAsync;
            services.AddSingleton(resolver);
        });
    }

    /// <summary>
    ///     Sets what ApplyAllDatabaseChangesOnStartup sets; Marten keeps the flag internal.
    /// </summary>
    private static void SetStartupFlag(StoreOptions options, string name, bool value) =>
        (typeof(StoreOptions).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
         throw new InvalidOperationException($"StoreOptions has no {name} property.")).SetValue(options, value);

    private static Task<IPAddress[]> ResolveOfflineAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IPAddress[]>(host == TestPagesHost
            ? [IPAddress.Loopback]
            : [IPAddress.Parse("10.0.0.1")]);

    /// <summary>
    ///     The real policy, plus the loopback the page server listens on.
    /// </summary>
    private sealed class LoopbackAllowingAddressPolicy : ILinkPreviewAddressPolicy
    {
        public bool Allows(IPAddress address) =>
            IPAddress.IsLoopback(address) || LinkPreviewAddressPolicy.IsPublic(address);
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
