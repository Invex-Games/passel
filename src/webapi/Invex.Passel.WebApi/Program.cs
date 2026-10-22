var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWebDefaults();

builder.AddNpgsqlDataSource(connectionName: "passel-db");

builder
    .Services
    .AddMarten(storeOptions =>
    {
        storeOptions.Projections.Snapshot<PacketAggregate>(SnapshotLifecycle.Inline);

        // The feed query filters on the user and pages backwards on the id, so one composite index serves it as a
        // bounded backward range scan however many packets the user has. Marten emits it over the "UserId" JSON
        // expression and the id column; see docs/components/web-api.md for the DDL and the measured plans. The
        // name is set because the derived one runs the members together ("user_idid")
        storeOptions
            .Schema
            .For<PacketAggregate>()
            .Index([p => p.UserId, p => p.Id], index => index.Name = "mt_doc_packetaggregate_idx_user_id_id");

        // AutoCreate.All drops and recreates mismatched tables, which is only acceptable for local data
        storeOptions.AutoCreateSchemaObjects = builder.Environment.IsDevelopment()
            ? AutoCreate.All
            : AutoCreate.CreateOrUpdate;

        // jsonb reorders keys, so the "$type" discriminator is not guaranteed to come first
        storeOptions.UseSystemTextJsonForSerialization(configure: jsonOptions =>
            jsonOptions.AllowOutOfOrderMetadataProperties = true);
    })
    .UseLightweightSessions()
    .UseNpgsqlDataSource();

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
    .AddSingleton<IIdEncodingService, IdEncodingService>()
    .AddScoped<IPacketStoreService, PacketStoreService>()
    .AddSingleton<IPacketQuotaService, PacketQuotaService>()
    .AddSingleton<IPacketEventHub, PacketEventHub>()
    .AddHostedService<PacketNotificationListener>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseWebDefaults();

app.MapPacketEndpoints();

if (app.Environment.IsDevelopment())
    app.MapDevApi();

app.Run();
