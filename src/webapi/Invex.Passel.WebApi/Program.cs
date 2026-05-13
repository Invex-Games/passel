var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWebDefaults();

builder.AddNpgsqlDataSource(connectionName: "passel-db");

builder
    .Services
    .AddMarten(storeOptions =>
    {
        storeOptions.Projections.Add<PacketAggregate>(ProjectionLifecycle.Inline);
        storeOptions.AutoCreateSchemaObjects = AutoCreate.All;
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
