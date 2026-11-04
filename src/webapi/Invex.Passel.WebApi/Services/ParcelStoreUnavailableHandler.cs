using System.Globalization;
using Azure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Answers a failure of the file storage with a 503 problem saying to try again shortly: an error from the storage
///     service (<see cref="RequestFailedException" />, alone or several of them after the client's retries) or a host
///     with no storage configured (<see cref="ParcelStoreUnavailableException" />). Only file requests reach the
///     storage, so texts and links keep working through an outage. Logged at Warning with the status and error code
///     only: the storage's own messages carry blob addresses.
/// </summary>
internal sealed partial class ParcelStoreUnavailableHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ParcelStoreUnavailableHandler> logger) : IExceptionHandler
{
    /// <summary>
    ///     How long a client is asked to wait before trying again, in seconds.
    /// </summary>
    internal const int RetryAfterSeconds = 30;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsStorageFailure(exception))
            return false;

        var failed = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions[0]
            : exception;

        if (failed is RequestFailedException requestFailed)
            LogStorageFailed(httpContext.Request.Method, httpContext.Request.Path, requestFailed.Status,
                requestFailed.ErrorCode);
        else
            LogStorageMissing(httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // Without a problem (the client does not accept JSON) the bare status code is the answer
        await problemDetailsService.TryWriteAsync(new()
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "File storage unavailable",
                Detail = "Files can't be stored or fetched right now. Try again shortly.",
            },
        });

        return true;
    }

    /// <summary>
    ///     Whether <paramref name="exception" /> is a failure of the file storage, which this handler answers.
    /// </summary>
    internal static bool IsStorageFailure(Exception exception) =>
        exception switch
        {
            RequestFailedException or ParcelStoreUnavailableException => true,
            AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.InnerExceptions.All(IsStorageFailure),
            _ => false,
        };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "File storage failed a request to {Method} {Path} with {Status} {ErrorCode}")]
    private partial void LogStorageFailed(string method, string path, int status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A request to {Method} {Path} needed file storage, and none is configured")]
    private partial void LogStorageMissing(string method, string path);
}
