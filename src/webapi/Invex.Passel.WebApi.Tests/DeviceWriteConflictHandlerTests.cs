using System.Text.Json;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invex.Passel.WebApi.Tests;

public sealed class DeviceWriteConflictHandlerTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static (DeviceWriteConflictHandler Handler, DefaultHttpContext HttpContext) Create()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };

        httpContext.Request.Method = HttpMethods.Patch;
        httpContext.Request.Path = "/api/devices/abc";
        httpContext.Request.Headers.Accept = "application/json";
        httpContext.Response.Body = new MemoryStream();

        return (new(services.GetRequiredService<IProblemDetailsService>(), NullLogger<DeviceWriteConflictHandler>.Instance),
            httpContext);
    }

    [Fact]
    public async Task A_write_that_kept_losing_races_is_a_conflict_problem()
    {
        var (handler, httpContext) = Create();

        var handled = await handler.TryHandleAsync(httpContext,
            new DeviceWriteConflictException(3, new InvalidOperationException("lost")),
            CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);

        httpContext.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(httpContext.Response.Body, cancellationToken: CancellationToken);

        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("Send it again", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Any_other_exception_is_left_to_the_exception_handler()
    {
        var (handler, httpContext) = Create();

        Assert.False(await handler.TryHandleAsync(httpContext, new InvalidOperationException(), CancellationToken));
        Assert.Equal(0, httpContext.Response.Body.Length);
    }
}
