using System.Reflection;
using Invex.Passel.WebApi.Extensions;
using Invex.Passel.WebApi.Services;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Invex.Passel.WebApi.Tests;

public sealed class DatabaseSetupTests
{
    /// <summary>
    ///     The store as <c>Program.cs</c> registers it in <paramref name="environment" />. Nothing connects: the
    ///     connection string points at a port no database listens on.
    /// </summary>
    private static WebApplication BuildHost(string environment)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });

        builder.Configuration[$"ConnectionStrings:{HostDatabaseExtensions.ConnectionName}"] =
            "Host=localhost;Port=1;Username=x;Password=x;Database=x";

        builder.AddPasselDatabase();

        return builder.Build();
    }

    /// <summary>
    ///     What ApplyAllDatabaseChangesOnStartup and AssertDatabaseMatchesConfigurationOnStartup set: Marten keeps the
    ///     flags internal, so they are read by name, and a rename fails here rather than passing unnoticed.
    /// </summary>
    private static bool StartupFlag(StoreOptions options, string name) =>
        (bool)(typeof(StoreOptions)
                   .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?.GetValue(options) ??
               throw new InvalidOperationException($"StoreOptions has no {name} property."));

    [Fact]
    public void Outside_development_the_api_never_changes_the_schema_and_checks_it_at_startup()
    {
        using var app = BuildHost(Environments.Production);
        var options = app.Services.GetRequiredService<StoreOptions>();

        Assert.Equal(AutoCreate.None, options.AutoCreateSchemaObjects);
        Assert.False(StartupFlag(options, "ShouldApplyChangesOnStartup"));

        // Its own check rather than Marten's, which refuses a schema a newer release has added to
        Assert.False(StartupFlag(options, "ShouldAssertDatabaseMatchesConfigurationOnStartup"));
        Assert.Contains(app.Services.GetServices<IHostedService>(), service => service is DatabaseSchemaCheck);
    }

    [Fact]
    public void Development_creates_and_updates_the_schema_at_startup()
    {
        using var app = BuildHost(Environments.Development);
        var options = app.Services.GetRequiredService<StoreOptions>();

        Assert.Equal(AutoCreate.All, options.AutoCreateSchemaObjects);
        Assert.True(StartupFlag(options, "ShouldApplyChangesOnStartup"));
        Assert.False(StartupFlag(options, "ShouldAssertDatabaseMatchesConfigurationOnStartup"));
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service is DatabaseSchemaCheck);
    }

    /// <summary>
    ///     Marten 9 generates no code at runtime, so there is no <c>codegen write</c> step to run or keep current.
    ///     Should a package start registering generated code (Wolverine handlers, say), this fails, and the build
    ///     needs that step and a check that its output is committed; see docs/components/web-api.md.
    /// </summary>
    [Fact]
    public void Nothing_registers_code_that_would_need_generating_ahead_of_time()
    {
        using var app = BuildHost(Environments.Production);

        Assert.Empty(app.Services.GetServices<ICodeFileCollection>());
    }

    [Theory]
    [InlineData("passel_api_identity-3f6pq2xk7vbnm")]
    [InlineData("passel_app")]
    [InlineData("public")]
    public void Role_and_schema_names_are_quoted(string name)
    {
        Assert.Equal($"\"{name}\"", DatabaseMigrator.QuoteIdentifier(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("api\"; drop table mt_doc_packet; --")]
    [InlineData("api role")]
    [InlineData("1api")]
    public void Anything_else_is_refused_rather_than_written_into_sql(string name)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseMigrator.QuoteIdentifier(name));
    }

    [Fact]
    public void The_entra_role_is_mapped_to_the_identity_by_its_object_id_and_is_not_an_administrator()
    {
        var objectId = Guid.NewGuid();

        var sql = DatabaseMigrator.CreateEntraRoleSql("passel_api_identity-abc", objectId.ToString());

        Assert.Contains("create role \"passel_api_identity-abc\" login;", sql);
        Assert.Contains(
            $"security label for \"pgaadauth\" on role \"passel_api_identity-abc\" is 'aadauth,oid={objectId:D},type=service';",
            sql);
        Assert.DoesNotContain("admin", sql.Replace("pgaadauth", string.Empty));
    }

    [Fact]
    public void An_object_id_that_is_not_a_guid_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrator.CreateEntraRoleSql("passel_api_identity-abc", "'; drop role postgres; --"));
    }
}
