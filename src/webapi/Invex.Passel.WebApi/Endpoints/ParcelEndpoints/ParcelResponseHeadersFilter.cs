using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Primitives;

namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

/// <summary>
///     The headers every file response carries, whatever its status: problems and errors included, so nothing a user
///     uploaded is ever run as a page or sniffed into another type. The bytes are the user's own, so the policy
///     sandboxes them and allows them nothing; a browser that opens a file it was given still cannot run its scripts.
/// </summary>
/// <remarks>
///     A file is immutable once uploaded, so a successful answer may be cached for a year, by the user's browser only
///     (<c>private</c>) and per token (<c>Vary: Authorization</c>); anything else is not cached at all. Responses are
///     never compressed: compression would drop the length and break ranges, and weaken the strong entity tag.
/// </remarks>
internal sealed class ParcelResponseHeadersFilter : IEndpointFilter
{
    internal const string ContentSecurityPolicy = "sandbox; default-src 'none'";
    internal const string ImmutableCacheControl = "private, max-age=31536000, immutable";
    internal const string NoStoreCacheControl = "no-store";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (httpContext.Features.Get<IHttpsCompressionFeature>() is { } compression)
            compression.Mode = HttpsCompressionMode.DoNotCompress;

        // When the response starts, so the status is known and the headers hold however the response ended: an
        // exception handled further out still answers with them
        httpContext.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            var headers = response.Headers;

            headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";

            if (response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent
                or StatusCodes.Status304NotModified)
            {
                headers[HeaderNames.CacheControl] = ImmutableCacheControl;
                headers[HeaderNames.Vary] = StringValues.Concat(headers[HeaderNames.Vary], HeaderNames.Authorization);
            }
            else
            {
                headers[HeaderNames.CacheControl] = NoStoreCacheControl;
            }

            return Task.CompletedTask;
        }, httpContext.Response);

        return next(context);
    }
}
