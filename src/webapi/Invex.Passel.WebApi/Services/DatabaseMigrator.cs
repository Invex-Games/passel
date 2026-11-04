using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using Weasel.Core;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Brings a database up to the schema the store is configured with, and gives the API's own role exactly the
///     privileges it runs with: reading and writing rows, never changing the schema. Runs as the deployment's
///     migration job (the <c>db-migrate</c> command), which logs in as a role allowed to change the schema; outside
///     Development the API itself never does (<c>AutoCreate.None</c>), so it can run on the DML-only role.
/// </summary>
internal sealed partial class DatabaseMigrator(
    IDocumentStore store,
    NpgsqlDataSource dataSource,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger)
{
    /// <summary>
    ///     The role that holds the API's privileges on the store's tables, sequences and functions. It cannot log in;
    ///     the API's own login role is made a member of it.
    /// </summary>
    internal const string ApplicationRole = "passel_app";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        // Add-only: CreateOrUpdate would drop every column, index and foreign key the configuration no longer
        // declares, so a rollback to an older release would delete what a newer one added, and the data in it. Left in
        // place, they cost nothing: the older API's startup check accepts what it doesn't know. Removing one is a
        // deliberate change by hand (docs/guides/deploying.md). Marten builds its schema objects once per store, so
        // the flag set here is the one the migration below reads
        foreach (var table in store.Storage.AllObjects().OfType<ITable>())
            table.AddOnlyMigrations = true;

        var migration = await store.Storage.Database.CreateMigrationAsync(cancellationToken);

        foreach (var delta in migration.Deltas.OfType<ISchemaObjectDeltaWithWithheldDrops>())
        foreach (var withheld in delta.WithheldDrops)
            LogDropWithheld(withheld, ((ISchemaObjectDelta)delta).SchemaObject.Identifier.QualifiedName);

        // The configured AutoCreate is None outside Development, so the threshold is raised for this call only.
        // CreateOrUpdate adds and changes, and never drops a table
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate);
        LogSchemaApplied(store.Options.DatabaseSchemaName);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await ExecuteAsync(connection, GrantApplicationRoleSql(store.Options.DatabaseSchemaName), cancellationToken);

        if (options.Value.ApiRole is not { Length: > 0 } apiRole)
        {
            LogNoApiRole();

            return;
        }

        if (options.Value.ApiRoleObjectId is { Length: > 0 } objectId)
            await ExecuteAsync(connection, CreateEntraRoleSql(apiRole, objectId), cancellationToken);

        await ExecuteAsync(connection, $"grant {ApplicationRole} to {QuoteIdentifier(apiRole)}", cancellationToken);
        LogApiRoleGranted(apiRole);
    }

    /// <summary>
    ///     Creates the application role if needed and grants it DML on everything in the schema, now and (through
    ///     default privileges) on what this login creates in it later. Every statement is idempotent.
    /// </summary>
    internal static string GrantApplicationRoleSql(string schema)
    {
        var quotedSchema = QuoteIdentifier(schema);

        return $"""
            do $$
            begin
                if not exists (select from pg_catalog.pg_roles where rolname = '{ApplicationRole}') then
                    create role {ApplicationRole} nologin;
                end if;
            end
            $$;
            grant usage on schema {quotedSchema} to {ApplicationRole};
            grant select, insert, update, delete on all tables in schema {quotedSchema} to {ApplicationRole};
            grant usage, select on all sequences in schema {quotedSchema} to {ApplicationRole};
            grant execute on all functions in schema {quotedSchema} to {ApplicationRole};
            alter default privileges in schema {quotedSchema} grant select, insert, update, delete on tables to {ApplicationRole};
            alter default privileges in schema {quotedSchema} grant usage, select on sequences to {ApplicationRole};
            alter default privileges in schema {quotedSchema} grant execute on functions to {ApplicationRole};
            """;
    }

    /// <summary>
    ///     Creates <paramref name="role" /> as a login if it does not exist and maps it to the Microsoft Entra
    ///     managed identity <paramref name="objectId" />, the way Azure Database for PostgreSQL documents for
    ///     enabling Entra authentication on a role. Not an Entra administrator, so the role has only what it is
    ///     granted.
    /// </summary>
    internal static string CreateEntraRoleSql(string role, string objectId)
    {
        if (!Guid.TryParse(objectId, out var oid))
            throw new InvalidOperationException($"Database:ApiRoleObjectId '{objectId}' is not an object id.");

        var quotedRole = QuoteIdentifier(role);

        return $"""
            do $$
            begin
                if not exists (select from pg_catalog.pg_roles where rolname = {QuoteLiteral(role)}) then
                    create role {quotedRole} login;
                end if;
            end
            $$;
            security label for "pgaadauth" on role {quotedRole} is 'aadauth,oid={oid:D},type=service';
            """;
    }

    /// <summary>
    ///     Quotes a role or schema name. Only the characters Azure gives resource names (letters, digits, '_', '-'
    ///     and '.') are accepted, so a misconfigured value fails rather than ending up in SQL.
    /// </summary>
    internal static string QuoteIdentifier(string name) =>
        IdentifierPattern().IsMatch(name)
            ? $"\"{name}\""
            : throw new InvalidOperationException($"'{name}' is not a role or schema name this migration accepts.");

    private static string QuoteLiteral(string name) =>
        IdentifierPattern().IsMatch(name)
            ? $"'{name}'"
            : throw new InvalidOperationException($"'{name}' is not a role or schema name this migration accepts.");

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex IdentifierPattern();

    [LoggerMessage(LogLevel.Information,
        "Left the {Withheld} of {Table} in place, which the configuration no longer declares; drop it by hand if it is no longer needed")]
    private partial void LogDropWithheld(string withheld, string table);

    [LoggerMessage(LogLevel.Information, "Applied the configured schema to the '{Schema}' schema")]
    private partial void LogSchemaApplied(string schema);

    [LoggerMessage(LogLevel.Warning, "Database:ApiRole is not set, so no role was granted the API's privileges")]
    private partial void LogNoApiRole();

    [LoggerMessage(LogLevel.Information, "Granted the privileges of " + ApplicationRole + " to the role {ApiRole}")]
    private partial void LogApiRoleGranted(string apiRole);
}
