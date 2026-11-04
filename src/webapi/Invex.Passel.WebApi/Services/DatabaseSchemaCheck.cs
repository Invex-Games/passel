using Weasel.Core;
using Weasel.Postgresql.Tables;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Outside Development, checks before anything else starts that the database has what this build needs: every
///     schema, table, column and function the store is configured with. A revision whose migration never ran
///     therefore fails to start and never takes traffic. Anything beyond that is accepted and logged: a newer
///     release's migration may have added columns or indexes, or changed Marten's functions, and the previous
///     revision must still start on that schema, because it keeps serving (and restarting) until the new revision is
///     ready, and for good when the rest of the deployment fails. Marten's own
///     <c>AssertDatabaseMatchesConfigurationOnStartup</c> refuses any difference, which would turn a deployment that
///     failed after its migration into an outage at the next restart.
/// </summary>
internal sealed partial class DatabaseSchemaCheck(IDocumentStore store, ILogger<DatabaseSchemaCheck> logger)
    : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var migration = await store.Storage.Database.CreateMigrationAsync(cancellationToken);

        if (Missing(migration) is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"The database lacks what this build of the API needs, so it does not start; db-migrate applies the schema. Missing: {string.Join("; ", missing)}.");

        foreach (var delta in migration.Deltas.Where(delta => delta.Difference != SchemaPatchDifference.None))
            LogDifferenceAccepted(delta.SchemaObject.Identifier.QualifiedName, delta.Difference);
    }

    /// <summary>
    ///     What the configuration declares and the database lacks: a schema object that does not exist at all, or a
    ///     column of a table that does. Everything else a migration would change is not in the way of this build.
    /// </summary>
    internal static List<string> Missing(SchemaMigration migration)
    {
        List<string> missing = [];

        foreach (var delta in migration.Deltas)
        {
            if (delta.Difference == SchemaPatchDifference.Create)
            {
                missing.Add(delta.SchemaObject.Identifier.QualifiedName);

                continue;
            }

            if (delta is TableDelta { Actual: { } actual } table)
                missing.AddRange(table
                    .Expected
                    .Columns
                    .Where(column => !actual.HasColumn(column.Name))
                    .Select(column => $"column {column.Name} of {table.Expected.Identifier.QualifiedName}"));
        }

        return missing;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    [LoggerMessage(LogLevel.Information,
        "The database's {SchemaObject} differs from this build's configuration ({Difference}), with nothing missing that it needs; a newer release's migration may have changed it")]
    private partial void LogDifferenceAccepted(string schemaObject, SchemaPatchDifference difference);
}
