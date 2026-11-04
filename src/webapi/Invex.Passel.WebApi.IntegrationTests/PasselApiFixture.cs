using Azure.Storage.Blobs;
using Npgsql;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     One PostgreSQL container and one Azurite container for the test run, and the API running against the default
///     database and the blob storage. Tests act as users of their own (<see cref="User" />), so they share both
///     without seeing each other's packets or files.
/// </summary>
public sealed class PasselApiFixture : IAsyncLifetime
{
    /// <summary>
    ///     The image the AppHost runs (Aspire's default for <c>AddPostgres</c>), so the tests see the same server as
    ///     local development does.
    /// </summary>
    private const string PostgresImage = "postgres:18.3";

    /// <summary>
    ///     The emulator image the AppHost runs (Aspire.Hosting.Azure.Storage's default), for the same reason.
    /// </summary>
    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

    /// <summary>
    ///     The container the API stores files in, by default.
    /// </summary>
    private const string ParcelContainerName = "parcels";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage).Build();

    // The storage SDK asks for a newer service version than this Azurite knows of, which it refuses unless told not
    // to check; the AppHost's emulator is started with the same flag
    private readonly AzuriteContainer _azurite = new AzuriteBuilder(AzuriteImage)
        .WithInMemoryPersistence()
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private PasselApiFactory? _api;

    /// <summary>
    ///     The API, started against the container's default database, with live updates known to flow.
    /// </summary>
    internal PasselApiFactory Api =>
        _api ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>
    ///     The connection string of the Azurite container, for tests that use the blob store directly.
    /// </summary>
    internal string BlobConnectionString =>
        _azurite.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(_container.StartAsync(cancellationToken), _azurite.StartAsync(cancellationToken));

        // As the deployment provisions it; the store would create it on the first upload otherwise
        await new BlobServiceClient(BlobConnectionString)
            .GetBlobContainerClient(ParcelContainerName)
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        _api = new(_container.GetConnectionString(), blobConnectionString: BlobConnectionString);
        await _api.WaitUntilLiveUpdatesFlowAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();

        await _container.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    /// <summary>
    ///     Creates a database of its own on the container, with nothing in it, and returns a connection string for it.
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        var connectionString = _container.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var create = new NpgsqlCommand($"create database \"{name}\"", connection);
        await create.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = name,
        }.ConnectionString;
    }

    /// <summary>
    ///     A user id no other test uses, so a test sees only the packets it creates itself.
    /// </summary>
    public static string User(string name) =>
        $"{name}-{Guid.NewGuid():N}";
}
