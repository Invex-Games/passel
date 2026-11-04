using System.Net;
using Invex.Passel.WebApi.Extensions;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The deployment's path: the migration (<c>db-migrate</c>) applies the schema as the owner, and the API then runs
///     on a role that may read and write rows but not change the schema.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class DatabaseMigrationTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_api_runs_on_a_dml_only_role_after_the_migration()
    {
        var ownerConnectionString = await fixture.CreateEmptyDatabaseAsync("passel_migrated", CancellationToken);
        var apiConnectionString = await CreateLoginRoleAsync(ownerConnectionString, "passel_api_migrated");

        // Twice, as every deployment runs it: the second run must find nothing to do and fail on nothing
        await MigrateAsync(ownerConnectionString, "passel_api_migrated");
        await MigrateAsync(ownerConnectionString, "passel_api_migrated");

        await using var api = new PasselApiFactory(apiConnectionString, migratedSchema: true);
        using var client = api.CreateClientFor(PasselApiFixture.User("migrated"));

        // Creates (with the quota check's plain SQL), lists, notifications through LISTEN and NOTIFY, and deletes
        await api.WaitUntilLiveUpdatesFlowAsync(CancellationToken);

        var created = await client.CreateTextAsync("Migrated", "sent through the DML-only role", CancellationToken);

        Assert.Equal([created], (await client.ListAsync(null, CancellationToken)).Packets);
        Assert.Equal(HttpStatusCode.NoContent, await client.DeletePacketAsync(created.Id, CancellationToken));

        // And the role it runs on cannot change the schema
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(apiConnectionString, "create table passel_not_allowed (id int)"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(apiConnectionString, "alter table mt_doc_packet add column passel_not_allowed int"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task Without_the_migration_the_api_does_not_start()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync("passel_unmigrated", CancellationToken);

        await using var api = new PasselApiFactory(connectionString, migratedSchema: true);

        // Starting the host checks the schema, and finds none
        Assert.ThrowsAny<Exception>(() => api.CreateClient());

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken);

        await using var count = new NpgsqlCommand(
            "select count(*) from information_schema.tables where table_schema = 'public'",
            connection);

        // It changed nothing on the way
        Assert.Equal(0L, await count.ExecuteScalarAsync(CancellationToken));
    }

    /// <summary>
    ///     What <c>db-migrate</c> does: the API's host, built but not started, running the migrator as the owner.
    /// </summary>
    private static async Task MigrateAsync(string ownerConnectionString, string apiRole)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration[$"ConnectionStrings:{HostDatabaseExtensions.ConnectionName}"] = ownerConnectionString;
        builder.Configuration["Database:ApiRole"] = apiRole;
        builder.Configuration["Logging:LogLevel:Default"] = "Warning";

        builder.AddPasselDatabase();

        await using var app = builder.Build();

        await app
            .Services
            .GetRequiredService<DatabaseMigrator>()
            .MigrateAsync(CancellationToken);
    }

    /// <summary>
    ///     A role that can log in with a password and holds nothing until the migration grants it the API's
    ///     privileges, as the managed identity's role does in Azure.
    /// </summary>
    private static async Task<string> CreateLoginRoleAsync(string ownerConnectionString, string role)
    {
        const string password = "passel-api";

        await ExecuteAsync(ownerConnectionString, $"create role {role} login password '{password}'");

        return new NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = role,
            Password = password,
        }.ConnectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
