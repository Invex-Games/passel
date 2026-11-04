using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Invex.Passel.WebApi.Extensions;

internal static class HostParcelExtensions
{
    /// <summary>
    ///     The connection string name of the blob storage that holds uploaded files, which the AppHost and the
    ///     deployment both supply.
    /// </summary>
    public const string BlobConnectionName = "passel-blobs";

    /// <summary>
    ///     The setting that picks the store, <see cref="ParcelOptions.Store" />.
    /// </summary>
    public const string StoreKey = $"{ParcelOptions.SectionName}:{nameof(ParcelOptions.Store)}";

    /// <summary>
    ///     The directory the filesystem store keeps the files in, <see cref="ParcelOptions.FileSystemRoot" />.
    /// </summary>
    public const string FileSystemRootKey = $"{ParcelOptions.SectionName}:{nameof(ParcelOptions.FileSystemRoot)}";

    /// <summary>
    ///     Uploaded files: their options, the store their bytes go to (<see cref="StoreKey" />), what checks and thumbnails them on the way
    ///     in, the quota on them, and the sweep that expires and erases them. Kept apart from <see cref="HostDatabaseExtensions.AddPasselDatabase" />,
    ///     which hosts without storage call too.
    /// </summary>
    public static void AddPasselParcels(this WebApplicationBuilder builder)
    {
        var blobConnectionConfigured = builder.Configuration.GetConnectionString(BlobConnectionName) is { Length: > 0 };

        // Validated at startup, which a host without storage passes: only a store asked for by name and not set up
        // stops the API. The db-migrate job never starts the host, so it passes whatever the settings
        builder
            .Services
            .AddOptions<ParcelOptions>()
            .BindConfiguration(ParcelOptions.SectionName)
            .PostConfigure(options => options.BlobConnectionConfigured = blobConnectionConfigured)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);

        var store = builder.Configuration.GetValue(StoreKey, ParcelStoreKind.Auto);

        if (store == ParcelStoreKind.FileSystem)
        {
            // Touches no disk until it is used, so a host built without the volume (db-migrate) builds all the same
            builder.Services.AddSingleton<IParcelStore, FileSystemParcelStore>();
        }
        else if (store is ParcelStoreKind.AzureBlob or ParcelStoreKind.Auto && blobConnectionConfigured)
        {
            // No health check: /health is the readiness probe of the only replica, and a storage outage must not take
            // texts and links down with it. Storage failures surface as 503 on the file endpoints instead
            builder.AddAzureBlobServiceClient(BlobConnectionName, settings => settings.DisableHealthChecks = true);
            builder.Services.AddSingleton<IParcelStore, AzureBlobParcelStore>();
        }
        else
        {
            // The db-migrate job runs this whole file without storage settings, and must still build; on a host that
            // serves requests without them, every file request answers 503. An explicit AzureBlob without its
            // connection string ends up here too, and fails the validation of the options at startup
            builder.Services.AddSingleton<IParcelStore, UnavailableParcelStore>();
        }

        builder
            .Services
            .AddSingleton<ParcelInspector>()
            .AddSingleton<ParcelUploadGate>()
            .AddSingleton<IParcelQuotaService, ParcelQuotaService>()
            .AddSingleton<ParcelSweepSignal>()
            .AddSingleton<IParcelSweepSignal>(services => services.GetRequiredService<ParcelSweepSignal>())
            .AddScoped<IParcelStoreService, ParcelStoreService>()
            .AddScoped<ParcelSweep>()
            .AddScoped<IParcelSweep>(services => services.GetRequiredService<ParcelSweep>())
            .AddHostedService<ParcelSweepWorker>();
    }
}
