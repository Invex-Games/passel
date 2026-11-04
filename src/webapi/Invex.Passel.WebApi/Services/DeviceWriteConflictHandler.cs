using Microsoft.AspNetCore.Diagnostics;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Answers a <see cref="DeviceWriteConflictException" /> with a 409 problem saying to try again. A handled
///     exception is not logged by the exception handler middleware, so this logs it, at Warning: it is contention,
///     not a fault.
/// </summary>
internal sealed partial class DeviceWriteConflictHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DeviceWriteConflictHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DeviceWriteConflictException conflict)
            return false;

        LogConflict(conflict, httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        // Without a problem (the client does not accept JSON) the bare status code is the answer
        await problemDetailsService.TryWriteAsync(new()
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Device changed",
                Detail = "The device was changed by other requests while this one was handled. Send it again.",
            },
        });

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A device write to {Method} {Path} kept losing races")]
    private partial void LogConflict(Exception exception, string method, string path);
}
