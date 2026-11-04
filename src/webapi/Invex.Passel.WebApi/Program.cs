var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWebDefaults();

builder.AddPasselDatabase();

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

// The device registry: what the push sender (roadmap 2.B1) will deliver to, and what sessions (1.B1) will belong to.
// A write that kept losing races is a 409, not a 500
builder
    .Services
    .AddScoped<IDeviceStoreService, DeviceStoreService>()
    .AddExceptionHandler<DeviceWriteConflictHandler>();

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

// A JasperFx command when the first argument names one: db-migrate (what the deployment runs before each release),
// db-assert, db-patch and the rest; see docs/components/web-api.md. Otherwise the API itself, with whatever options
// the host has already read from the arguments (JasperFx would reject those, and the test host passes its settings so)
if (args is [var command, ..] && !command.StartsWith('-'))
    return await app.RunJasperFxCommands(args);

await app.RunAsync();

return 0;
