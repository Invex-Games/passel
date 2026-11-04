using Marten.Schema;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWebDefaults();

builder.AddNpgsqlDataSource(connectionName: "passel-db");

builder
    .Services
    .AddMarten(storeOptions =>
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

        // AutoCreate.All drops and recreates mismatched tables, which is only acceptable for local data
        storeOptions.AutoCreateSchemaObjects = builder.Environment.IsDevelopment()
            ? AutoCreate.All
            : AutoCreate.CreateOrUpdate;

        // jsonb reorders keys, so the "$type" discriminator of the content is not guaranteed to come first
        storeOptions.UseSystemTextJsonForSerialization(configure: jsonOptions =>
            jsonOptions.AllowOutOfOrderMetadataProperties = true);
    })
    .UseLightweightSessions()
    .UseNpgsqlDataSource()
    // The packet table exists before the first request: the quota check reads it with plain SQL, which Marten's
    // lazy, on-first-use schema creation would not cover
    .ApplyAllDatabaseChangesOnStartup();

builder
    .Services
    .AddOptions<QuotaOptions>()
    .BindConfiguration(QuotaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder
    .Services
    .AddOptions<EventStreamOptions>()
    .BindConfiguration(EventStreamOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder
    .Services
    .AddOptions<LinkPreviewOptions>()
    .BindConfiguration(LinkPreviewOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder
    .Services
    .AddSingleton<IIdEncodingService, IdEncodingService>()
    .AddScoped<IPacketStoreService, PacketStoreService>()
    .AddSingleton<IPacketQuotaService, PacketQuotaService>()
    .AddSingleton<IPacketEventHub, PacketEventHub>()
    .AddHostedService<PacketNotificationListener>();

// The device registry: what the push sender (roadmap 2.B1) will deliver to, and what sessions (1.B1) will belong to
builder
    .Services
    .AddScoped<IDeviceStoreService, DeviceStoreService>();

// Link previews: the page of a new link is fetched in the background, behind guards that keep the fetcher on the
// public internet (LinkPreviewAddressPolicy), and attached to the link with a packet-updated event
builder
    .Services
    .AddSingleton<ILinkPreviewAddressPolicy, LinkPreviewAddressPolicy>()
    .AddSingleton<ILinkPreviewFetcher, LinkPreviewFetcher>()
    .AddSingleton<LinkPreviewQueue>()
    .AddSingleton<ILinkPreviewQueue>(services => services.GetRequiredService<LinkPreviewQueue>())
    .AddHostedService<LinkPreviewWorker>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseWebDefaults();

app.MapPacketEndpoints();
app.MapDeviceEndpoints();

if (app.Environment.IsDevelopment())
    app.MapDevApi();

app.Run();
