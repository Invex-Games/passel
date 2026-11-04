using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     What the telemetry <c>ServiceDefaults</c> configures keeps of outgoing requests, observed through a processor
///     and a metric reader added to the same providers the exporters use. The requests are real, through the
///     runtime's handler, to a listener on the loopback.
/// </summary>
public sealed class TelemetryTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    /// <summary>
    ///     Records every span the tracer provider ends, which is every span an exporter would be given.
    /// </summary>
    private sealed class SpanCapture : BaseProcessor<Activity>
    {
        public ConcurrentQueue<Activity> Ended { get; } = [];

        public override void OnEnd(Activity data) =>
            Ended.Enqueue(data);
    }

    /// <summary>
    ///     Records the name and dimensions of every metric point an exporter would be given.
    /// </summary>
    private sealed class MetricCapture : BaseExporter<Metric>
    {
        public ConcurrentQueue<(string Name, string[] Tags)> Points { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new List<string>();

                foreach (var tag in point.Tags)
                    tags.Add(tag.Key);

                Points.Enqueue((metric.Name, [..tags]));
            }

            return ExportResult.Success;
        }
    }

    /// <summary>
    ///     Answers every request on the loopback with an empty 200 and closes the connection.
    /// </summary>
    private static async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();

            var buffer = new byte[4096];
            var received = new StringBuilder();

            while (!received.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                    break;

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                cancellationToken);
        }
    }

    [Fact]
    public async Task A_request_excluded_from_telemetry_leaves_no_span_and_metrics_name_no_host()
    {
        var spans = new SpanCapture();
        var metrics = new MetricCapture();
        using var reader = new BaseExportingMetricReader(metrics);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });

        builder.AddServiceDefaults();

        builder
            .Services
            .AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(spans))
            .WithMetrics(meters => meters.AddReader(reader));

        using var host = builder.Build();

        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using var stopServer = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var server = ServeAsync(listener, stopServer.Token);

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpMessageInvoker(new SocketsHttpHandler());

            using var traced = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/traced");
            (await http.SendAsync(traced, CancellationToken)).Dispose();

            using var excluded = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/document/d/secret/edit");
            excluded.ExcludeFromTelemetry();
            (await http.SendAsync(excluded, CancellationToken)).Dispose();
        }
        finally
        {
            await stopServer.CancelAsync();
            listener.Stop();

            // Ends by being cancelled, mid-accept
            await Record.ExceptionAsync(() => server);
        }

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        // The ordinary request is traced, so the capture would have seen the other one too
        var paths = spans
            .Ended
            .Where(span => span.Kind == ActivityKind.Client)
            .Select(span => span.GetTagItem("url.full") as string)
            .ToList();

        Assert.Contains(paths, url => url?.EndsWith("/traced", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(paths, url => url?.Contains("secret", StringComparison.Ordinal) is true);

        // Both requests are counted, without the host they went to
        var requestDurations = metrics.Points.Where(point => point.Name == "http.client.request.duration").ToList();

        Assert.NotEmpty(requestDurations);
        Assert.Contains(requestDurations, point => point.Tags.Contains("http.request.method"));

        Assert.DoesNotContain(metrics.Points,
            point => point.Tags.Any(tag =>
                tag is "server.address" or "server.port" or "network.peer.address" or "dns.question.name"));
    }
}
