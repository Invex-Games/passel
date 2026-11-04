// ReSharper disable CheckNamespace

using Azure.Monitor.OpenTelemetry.AspNetCore;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
[PublicAPI]
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    ///     Set by the Azure deployment, from the Application Insights resource.
    /// </summary>
    private const string ApplicationInsightsConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    ///     The packet event stream. A stream stays open for up to a day, so its request would hold an activity open,
    ///     and export one long empty trace per stream, until the client disconnects.
    /// </summary>
    private const string PacketEventsPath = "/api/packets/events";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        [PublicAPI]
        public TBuilder AddServiceDefaults()
        {
            builder.ConfigureOpenTelemetry();

            builder.AddDefaultHealthChecks();

            builder.Services.AddServiceDiscovery();

            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Turn on resilience by default
                http.AddStandardResilienceHandler();

                // Turn on service discovery by default
                http.AddServiceDiscovery();
            });

            // Uncomment the following to restrict the allowed schemes for service discovery.
            // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
            // {
            //     options.AllowedSchemes = ["https"];
            // });

            return builder;
        }

        [PublicAPI]
        public TBuilder ConfigureOpenTelemetry()
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder
                .Services
                .AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
                })
                .WithTracing(tracing =>
                {
                    tracing
                        .AddSource(builder.Environment.ApplicationName)
                        .AddAspNetCoreInstrumentation(tracing =>
                            // Exclude health check requests and the long-lived event streams from tracing
                            tracing.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                                !context.Request.Path.StartsWithSegments(AlivenessEndpointPath) &&
                                !context.Request.Path.StartsWithSegments(PacketEventsPath))
                        // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                        //.AddGrpcClientInstrumentation()
                        .AddHttpClientInstrumentation();
                });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        private TBuilder AddOpenTelemetryExporters()
        {
            // Application Insights in Azure (the deployment sets its connection string), the Aspire dashboard's OTLP
            // endpoint locally. One or the other: both exporters take over the whole OpenTelemetry pipeline
            if (!string.IsNullOrWhiteSpace(builder.Configuration[ApplicationInsightsConnectionStringKey]))
            {
                builder
                    .Services
                    .AddOpenTelemetry()
                    .UseAzureMonitor();
            }
            else if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                builder
                    .Services
                    .AddOpenTelemetry()
                    .UseOtlpExporter();
            }

            return builder;
        }

        [PublicAPI]
        public TBuilder AddDefaultHealthChecks()
        {
            builder
                .Services
                .AddHealthChecks()
                // Add a default liveness check to ensure app is responsive
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

            return builder;
        }
    }

    [PublicAPI]
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Mapped in every environment so hosting platforms can probe readiness/liveness. The default response
        // writer only returns the aggregate status ("Healthy"/"Unhealthy"), so no check details are exposed.
        // See https://aka.ms/dotnet/aspire/healthchecks before adding a detailed response writer.

        // All health checks (including the database) must pass for app to be considered ready to accept traffic
        app.MapHealthChecks(HealthEndpointPath);

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks(AlivenessEndpointPath,
            new()
            {
                Predicate = r => r.Tags.Contains("live"),
            });

        return app;
    }
}
