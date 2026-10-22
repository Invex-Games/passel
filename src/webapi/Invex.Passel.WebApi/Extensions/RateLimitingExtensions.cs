using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Extensions;

/// <summary>
///     The rate-limiting policies that packet endpoints opt into with <c>RequireRateLimiting</c>.
/// </summary>
internal static class RateLimitPolicies
{
    public const string Creates = "packet-creates";
    public const string Reads = "packet-reads";
    public const string Streams = "packet-streams";
}

internal static class RateLimitingExtensions
{
    private const int SegmentsPerWindow = 6;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Registers the per-user policies in <see cref="RateLimitPolicies" />. Requests are counted per signed-in
    ///     user, or per remote address when there is none, and rejected with a 429 problem.
    /// </summary>
    public static void AddPacketRateLimiting(this WebApplicationBuilder builder)
    {
        builder
            .Services
            .AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejected;

            options.AddPolicy(RateLimitPolicies.Creates, context => Partition(context, limits => limits.CreatesPerMinute));
            options.AddPolicy(RateLimitPolicies.Reads, context => Partition(context, limits => limits.ReadsPerMinute));
            options.AddPolicy(RateLimitPolicies.Streams, context => Partition(context, limits => limits.StreamsPerMinute));
        });
    }

    /// <summary>
    ///     The key a request is counted under: the signed-in user, or the remote address for anonymous requests. The
    ///     prefixes keep the two kinds of key apart.
    /// </summary>
    internal static string PartitionKey(ClaimsPrincipal user, IPAddress? remoteAddress)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is { Length: > 0 }
            ? $"user:{userId}"
            : $"ip:{remoteAddress?.ToString() ?? "unknown"}";
    }

    private static RateLimitPartition<string> Partition(
        HttpContext httpContext,
        Func<RateLimitOptions, int> permitLimit)
    {
        var limits = httpContext
            .RequestServices
            .GetRequiredService<IOptions<RateLimitOptions>>()
            .Value;

        return RateLimitPartition.GetSlidingWindowLimiter(
            PartitionKey(httpContext.User, httpContext.Connection.RemoteIpAddress),
            _ => new()
            {
                PermitLimit = permitLimit(limits),
                Window = Window,
                SegmentsPerWindow = SegmentsPerWindow,
                QueueLimit = 0,
            });
    }

    private static async ValueTask OnRejected(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // A window limiter that cannot queue gives no retry-after metadata, so the fallback is the window: after a
        // whole one the user is sure to have permits again
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var fromLease)
            ? fromLease
            : Window;

        httpContext.Response.Headers.RetryAfter = Math
            .Ceiling(retryAfter.TotalSeconds)
            .ToString(CultureInfo.InvariantCulture);

        // Falls back to the bare status code when the client does not accept JSON (for example an event-stream
        // request), which the status code pages middleware then turns into its plain response
        await httpContext
            .RequestServices
            .GetRequiredService<IProblemDetailsService>()
            .TryWriteAsync(new()
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = "You have sent too many requests in the last minute. Wait a moment and try again.",
                },
            });
    }
}
