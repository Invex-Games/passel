using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

public static partial class ParcelEndpoints
{
    /// <summary>
    ///     The entity tag of an image's thumbnail: the image's own tag with the thumbnail size, so a thumbnail made at
    ///     another size never matches.
    /// </summary>
    internal static EntityTagHeaderValue ThumbnailEntityTag(Parcel parcel, ParcelOptions options) =>
        new($"\"{parcel.Sha256}-t{options.ThumbnailSize}\"");

    public static void MapGetParcelThumbnail(this IEndpointRouteBuilder builder) =>
        builder
            .MapGet("/{id}/thumbnail",
                async (
                    string id,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IParcelStoreService parcelStoreService,
                    IParcelStore store,
                    IOptions<ParcelOptions> options,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var parcelId))
                        return Results.NotFound();

                    // Only an image that decoded has a thumbnail
                    if (await parcelStoreService.LoadReadableAsync(ownerId, parcelId, cancellationToken) is not
                        { HasThumbnail: true } parcel)
                        return Results.NotFound();

                    if (await store.OpenReadAsync(ParcelKeys.Thumbnail(parcel.Id), cancellationToken) is not { } content)
                    {
                        LogMissingBlob(loggerFactory.CreateLogger(typeof(ParcelEndpoints)), parcel.Id);

                        return Results.NotFound();
                    }

                    httpContext.Response.Headers.ContentDisposition =
                        ParcelDownload.Disposition(ParcelDownload.Inline, "thumbnail.webp");

                    // Small enough to need no ranges; If-None-Match is still answered with 304
                    return Results.Stream(content.Stream,
                        ParcelSniffer.Webp,
                        lastModified: parcel.CreatedAt,
                        entityTag: ThumbnailEntityTag(parcel, options.Value));
                })
            .Produces<Stream>(StatusCodes.Status200OK, ParcelSniffer.Webp)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireRateLimiting(RateLimitPolicies.ParcelReads);
}
