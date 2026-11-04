using Marten.Schema;
using Npgsql;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostDatabaseExtensions
{
    /// <summary>
    ///     The connection string name, which the AppHost and the deployment both supply.
    /// </summary>
    public const string ConnectionName = "passel-db";

    /// <summary>
    ///     The PostgreSQL data source and the Marten store over it. Development creates and updates the schema at
    ///     startup, so local work needs no extra step. Everywhere else the schema belongs to the deployment's
    ///     migration job (<see cref="MigrateDatabaseCommand" />): the API changes nothing (<c>AutoCreate.None</c>), so
    ///     its database role needs no more than DML, and it checks at startup that the schema it finds has what it
    ///     needs (<see cref="DatabaseSchemaCheck" />). See docs/components/web-api.md.
    /// </summary>
    public static void AddPasselDatabase(this WebApplicationBuilder builder)
    {
        // A connection string without a password (the Azure deployment) signs in with a Microsoft Entra token for
        // the managed identity; one with a password (the AppHost's container, the tests) is used as it is. Nothing
        // uses Kerberos, so Npgsql does not try GSS encryption first (and log that the library for it is missing)
        builder.AddAzureNpgsqlDataSource(ConnectionName,
            configureDataSourceBuilder: dataSource =>
                dataSource.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable);

        var isDevelopment = builder.Environment.IsDevelopment();

        var marten = builder
            .Services
            .AddMarten(storeOptions => ConfigureStore(storeOptions, isDevelopment))
            .UseLightweightSessions()
            .UseNpgsqlDataSource();

        // The packet table exists before the first request either way: the quota check reads it with plain SQL,
        // which Marten's lazy, on-first-use schema creation would not cover. Outside Development a schema that lacks
        // what this build needs stops the start, so a revision deployed without its migration never takes traffic;
        // one that a newer release's migration has added to does not (DatabaseSchemaCheck)
        if (isDevelopment)
            marten.ApplyAllDatabaseChangesOnStartup();
        else
            builder.Services.AddHostedService<DatabaseSchemaCheck>();

        builder
            .Services
            .AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName);

        builder.Services.AddSingleton<DatabaseMigrator>();
    }

    internal static void ConfigureStore(StoreOptions storeOptions, bool isDevelopment)
    {
        // Packets are plain documents (ADR 0001). The feed query filters on the user and pages backwards on the id,
        // so one composite index serves it as a bounded backward range scan however many packets the user has.
        // Marten emits it over the "UserId" JSON expression and the id column; see docs/components/web-api.md for
        // the DDL and the measured plans. The name is set because the derived one runs the members together
        // ("user_idid")
        storeOptions
            .Schema
            .For<Packet>()
            .Index([p => p.UserId, p => p.Id], index => index.Name = "mt_doc_packet_idx_user_id_id");

        // Devices are documents too, one per installation and user. The unique index on the user and the client's
        // installation id is what makes registering idempotent, and its leading member serves the device list and
        // the cap. The unique index on the push key keeps each push token on one device at a time, whichever user
        // registered it; the key is a hash, and devices without a token leave it null, which a unique index allows
        // any number of times. Each change rewrites the whole document, so optimistic concurrency stops one from
        // undoing another made since its read (a rename lost to a registration's new LastSeenAt); the store reads
        // again and retries
        storeOptions
            .Schema
            .For<Device>()
            .UseOptimisticConcurrency(true)
            .UniqueIndex(UniqueIndexType.Computed,
                "mt_doc_device_uidx_user_id_installation_id",
                d => d.UserId,
                d => d.InstallationId)
            .UniqueIndex(UniqueIndexType.Computed, "mt_doc_device_uidx_push_key", d => d.PushKey!);

        // AutoCreate.All drops and recreates mismatched tables, which is only acceptable for local data. Elsewhere the
        // API's role may not change the schema at all; db-migrate applies it
        storeOptions.AutoCreateSchemaObjects = isDevelopment
            ? AutoCreate.All
            : AutoCreate.None;

        // jsonb reorders keys, so the "$type" discriminator of the content is not guaranteed to come first
        storeOptions.UseSystemTextJsonForSerialization(configure: jsonOptions =>
            jsonOptions.AllowOutOfOrderMetadataProperties = true);
    }
}
