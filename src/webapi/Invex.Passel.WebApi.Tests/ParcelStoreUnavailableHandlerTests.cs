using System.Text.Json;
using Azure;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelStoreUnavailableHandlerTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static (ParcelStoreUnavailableHandler Handler, DefaultHttpContext HttpContext) Create()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };

        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/api/parcels/abc";
        httpContext.Request.Headers.Accept = "application/json";
        httpContext.Response.Body = new MemoryStream();

        return (new(services.GetRequiredService<IProblemDetailsService>(),
                NullLogger<ParcelStoreUnavailableHandler>.Instance),
            httpContext);
    }

    public static TheoryData<Exception> StorageFailures =>
    [
        new RequestFailedException(503, "Service unavailable", "ServerBusy", null),
        new RequestFailedException(403, "Forbidden", "AuthorizationPermissionMismatch", null),
        new ParcelStoreUnavailableException(),
        new ParcelStoreFailedException("write", "abc", new IOException("No space left on device", 28)),
        new AggregateException(new ParcelStoreFailedException("delete", "abc", new UnauthorizedAccessException()),
            new ParcelStoreFailedException("list", string.Empty, new IOException())),
        new AggregateException(new RequestFailedException(500, "Internal error"),
            new RequestFailedException(503, "Service unavailable")),
    ];

    [Theory]
    [MemberData(nameof(StorageFailures))]
    public async Task A_storage_failure_is_a_503_problem_asking_to_retry(Exception exception)
    {
        var (handler, httpContext) = Create();

        Assert.True(await handler.TryHandleAsync(httpContext, exception, CancellationToken));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        Assert.Equal("30", httpContext.Response.Headers.RetryAfter.ToString());

        httpContext.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(httpContext.Response.Body, cancellationToken: CancellationToken);

        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("File storage unavailable", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Any_other_exception_is_left_to_the_exception_handler()
    {
        var (handler, httpContext) = Create();

        Assert.False(await handler.TryHandleAsync(httpContext, new InvalidOperationException(), CancellationToken));
        Assert.False(await handler.TryHandleAsync(httpContext,
            new AggregateException(new RequestFailedException(503, "Service unavailable"), new InvalidOperationException()),
            CancellationToken));
        Assert.Equal(0, httpContext.Response.Body.Length);
    }

    /// <summary>
    ///     A client that stops sending its upload part way is an <see cref="IOException" /> too, and no storage
    ///     failure; only the errors the filesystem store wraps are.
    /// </summary>
    [Fact]
    public async Task A_bare_io_error_is_left_to_the_exception_handler()
    {
        var (handler, httpContext) = Create();

        Assert.False(await handler.TryHandleAsync(httpContext, new IOException("Unexpected end of request content."),
            CancellationToken));
        Assert.Equal(0, httpContext.Response.Body.Length);
    }
}
