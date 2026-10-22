using Npgsql;
using Testcontainers.PostgreSql;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     One PostgreSQL container for the test run, and the API running against its default database. Tests act as
///     users of their own (<see cref="User" />), so they share the database without seeing each other's packets.
/// </summary>
public sealed class PasselApiFixture : IAsyncLifetime
{
    /// <summary>
    ///     The image the AppHost runs (Aspire's default for <c>AddPostgres</c>), so the tests see the same server as
    ///     local development does.
    /// </summary>
    private const string PostgresImage = "postgres:18.3";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage).Build();

    private PasselApiFactory? _api;

    /// <summary>
    ///     The API, started against the container's default database, with live updates known to flow.
    /// </summary>
    internal PasselApiFactory Api =>
        _api ?? throw new InvalidOperationException("The fixture has not been initialised.");

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await _container.StartAsync(cancellationToken);

        _api = new(_container.GetConnectionString());
        await _api.WaitUntilLiveUpdatesFlowAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();

        await _container.DisposeAsync();
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
