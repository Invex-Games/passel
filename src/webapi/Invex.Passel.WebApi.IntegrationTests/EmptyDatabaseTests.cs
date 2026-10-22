using System.Net;
using Npgsql;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The API on a database that holds nothing yet, as a fresh deployment or a new local environment would.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class EmptyDatabaseTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_schema_is_created_on_an_empty_database_before_the_first_request()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync("passel_empty", CancellationToken);

        Assert.Equal(0, await CountTablesAsync(connectionString));

        await using var api = new PasselApiFactory(connectionString);
        using var client = api.CreateClientFor(PasselApiFixture.User("first"));

        // Creating a client starts the host, and the schema with it
        Assert.NotEqual(0, await CountTablesAsync(connectionString));

        // The very first request is a create: it is the one request that reads the packet table with plain SQL (the
        // quota check), so it must not depend on an earlier request having made the table exist
        var created = await client.CreateTextAsync("First", "the first packet on this database", CancellationToken);

        Assert.Equal([created], (await client.ListAsync(null, CancellationToken)).Packets);
        Assert.Equal(HttpStatusCode.NoContent, await client.DeletePacketAsync(created.Id, CancellationToken));

        using var health = await client.GetAsync("/health", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<long> CountTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken);

        await using var count = new NpgsqlCommand(
            "select count(*) from information_schema.tables where table_schema not in ('pg_catalog', 'information_schema')",
            connection);

        return (long)(await count.ExecuteScalarAsync(CancellationToken))!;
    }
}
