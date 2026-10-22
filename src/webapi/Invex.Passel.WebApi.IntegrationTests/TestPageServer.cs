using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A web server of the tests' own, on the loopback, serving the pages the preview tests link to and recording
///     every request it gets. The API reaches it as <see cref="PasselApiFactory.TestPagesHost" /> through the
///     factory's resolver, which is the one name that resolves to the loopback there.
/// </summary>
internal sealed class TestPageServer : IAsyncDisposable
{
    public sealed record Request(string Path, string? Host, string? UserAgent);

    public const string ArticleTitle = "The article";
    public const string ArticleDescription = "What the article says, in a sentence.";
    public const string ArticleImagePath = "/images/cover.png";

    /// <summary>
    ///     A page with Open Graph tags, and a title and description that must lose to them.
    /// </summary>
    private const string ArticleHtml = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <title>Article | Test pages</title>
            <meta property="og:title" content="{{ArticleTitle}}">
            <meta property="og:description" content="{{ArticleDescription}}">
            <meta property="og:image" content="{{ArticleImagePath}}">
            <meta name="description" content="Not the description shown">
        </head>
        <body><p>The article.</p></body>
        </html>
        """;

    /// <summary>
    ///     A page that says nothing about itself.
    /// </summary>
    private const string PlainHtml = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>   </title></head><body><p>Nothing to show.</p></body></html>
        """;

    private readonly WebApplication _app;
    private readonly TaskCompletionSource _slowRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TestPageServer(WebApplication app) =>
        _app = app;

    public ConcurrentQueue<Request> Requests { get; } = new();

    public int Port { get; private set; }

    /// <summary>
    ///     Completes once the slow page has been asked for; the page is not answered until
    ///     <see cref="ReleaseSlow" />.
    /// </summary>
    public Task SlowRequested =>
        _slowRequested.Task;

    public static async Task<TestPageServer> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        // The API's appsettings.json is copied next to the test assembly, and its AllowedHosts names the production
        // host, which the builder would read and answer every other host with 400
        builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = ["*"]);

        // Any free port on the loopback; the port is read back once the server listens
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new TestPageServer(app);
        server.Map(app);

        await app.StartAsync(cancellationToken);
        server.Port = new Uri(app.Urls.Single()).Port;

        return server;
    }

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            Requests.Enqueue(new(context.Request.Path, context.Request.Headers.Host, context.Request.Headers.UserAgent));

            await next(context);
        });

        app.MapGet("/article", () => Results.Content(ArticleHtml, "text/html; charset=utf-8"));
        app.MapGet("/plain", () => Results.Content(PlainHtml, "text/html; charset=utf-8"));
        app.MapGet("/data.json", () => Results.Content("""{"title":"Not a page"}""", "application/json"));

        app.MapGet("/slow",
            async () =>
            {
                _slowRequested.TrySetResult();
                await _slowReleased.Task;

                return Results.Content(ArticleHtml, "text/html; charset=utf-8");
            });
    }

    public void ReleaseSlow() =>
        _slowReleased.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        // A request still waiting on the slow page may end
        _slowReleased.TrySetResult();

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
