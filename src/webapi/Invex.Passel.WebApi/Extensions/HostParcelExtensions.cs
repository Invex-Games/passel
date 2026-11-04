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
    ///     Uploaded files: their options, the blob store their bytes go to, and what checks and thumbnails them on the
    ///     way in. Kept apart from <see cref="HostDatabaseExtensions.AddPasselDatabase" />, which hosts without
    ///     storage call too.
    /// </summary>
    public static void AddPasselParcels(this WebApplicationBuilder builder)
    {
        // The options need no connection settings, so validating them at startup cannot stop a host without storage
        builder
            .Services
            .AddOptions<ParcelOptions>()
            .BindConfiguration(ParcelOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);

        if (builder.Configuration.GetConnectionString(BlobConnectionName) is { Length: > 0 })
        {
            // No health check: /health is the readiness probe of the only replica, and a storage outage must not take
            // texts and links down with it. Storage failures surface as 503 on the file endpoints instead
            builder.AddAzureBlobServiceClient(BlobConnectionName, settings => settings.DisableHealthChecks = true);
            builder.Services.AddSingleton<IParcelStore, AzureBlobParcelStore>();
        }
        else
        {
            // The db-migrate job runs this whole file without storage settings, and must still build; on a host that
            // serves requests without them, every file request answers 503
            builder.Services.AddSingleton<IParcelStore, UnavailableParcelStore>();
        }

        builder
            .Services
            .AddSingleton<ParcelInspector>()
            .AddSingleton<ParcelUploadGate>()
            .AddSingleton<ParcelSweepSignal>()
            .AddSingleton<IParcelSweepSignal>(services => services.GetRequiredService<ParcelSweepSignal>());
    }
}
