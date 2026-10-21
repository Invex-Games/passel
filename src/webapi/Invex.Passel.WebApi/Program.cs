var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWebDefaults();

builder.AddNpgsqlDataSource(connectionName: "passel-db");

builder
    .Services
    .AddMarten(storeOptions =>
    {
        storeOptions.Projections.Snapshot<PacketAggregate>(SnapshotLifecycle.Inline);
        storeOptions.AutoCreateSchemaObjects = AutoCreate.All;

        // jsonb reorders keys, so the "$type" discriminator is not guaranteed to come first
        storeOptions.UseSystemTextJsonForSerialization(configure: jsonOptions =>
            jsonOptions.AllowOutOfOrderMetadataProperties = true);
    })
    .UseLightweightSessions()
    .UseNpgsqlDataSource();

builder
    .Services
    .AddSingleton<IIdEncodingService, IdEncodingService>()
    .AddScoped<IPacketStoreService, PacketStoreService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseWebDefaults();

app.MapPacketEndpoints();

if (app.Environment.IsDevelopment())
    app.MapDevApi();

app.Run();
