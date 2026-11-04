using JasperFx.CommandLine;

// JasperFx only looks for commands in assemblies that ask for it
[assembly: JasperFxAssembly]

namespace Invex.Passel.WebApi.Extensions;

/// <summary>
///     <c>dotnet Invex.Passel.WebApi.dll db-migrate</c>: what the deployment's migration job runs before a new
///     revision of the API starts. See <see cref="DatabaseMigrator" />.
/// </summary>
[Description("Applies the database schema and grants the API's role its DML-only privileges", Name = "db-migrate")]
[UsedImplicitly]
public sealed class MigrateDatabaseCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        // Builds the host without starting it, so none of the API's hosted services run
        using var host = input.BuildHost();

        await host
            .Services
            .GetRequiredService<DatabaseMigrator>()
            .MigrateAsync(CancellationToken.None);

        return true;
    }
}
