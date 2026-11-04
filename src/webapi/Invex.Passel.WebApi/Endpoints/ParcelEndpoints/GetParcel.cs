using Microsoft.Net.Http.Headers;

namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

public static partial class ParcelEndpoints
{
    /// <summary>
    ///     The entity tag of a file's download: the SHA-256 of its bytes, which never change.
    /// </summary>
    internal static EntityTagHeaderValue EntityTag(Parcel parcel) =>
        new($"\"{parcel.Sha256}\"");

    public static void MapGetParcel(this IEndpointRouteBuilder builder) =>
        builder
            .MapGet("/{id}",
                async (
                    string id,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IParcelStoreService parcelStoreService,
                    IParcelStore store,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var parcelId))
                        return Results.NotFound();

                    // Another user's file, one still uploading and one deleted are all missing alike
                    if (await parcelStoreService.LoadReadableAsync(ownerId, parcelId, cancellationToken) is not { } parcel)
                        return Results.NotFound();

                    if (await store.OpenReadAsync(ParcelKeys.Original(parcel.Id), cancellationToken) is not { } content)
                    {
                        LogMissingBlob(loggerFactory.CreateLogger(typeof(ParcelEndpoints)), parcel.Id);

                        return Results.NotFound();
                    }

                    httpContext.Response.Headers.ContentDisposition = ParcelDownload.Disposition(parcel);

                    // The result answers ranges (206 and 416), If-Range and If-None-Match (304) from the stream's
                    // length and the tag, and disposes the stream
                    return Results.Stream(content.Stream,
                        parcel.ContentType,
                        lastModified: parcel.CreatedAt,
                        entityTag: EntityTag(parcel),
                        enableRangeProcessing: true);
                })
            // The bytes, of whatever type the file was sniffed as; described as a binary stream
            .Produces<Stream>(StatusCodes.Status200OK, ParcelSniffer.OctetStream)
            .Produces<Stream>(StatusCodes.Status206PartialContent, ParcelSniffer.OctetStream)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireRateLimiting(RateLimitPolicies.ParcelReads);

    // The id only: a file name is never logged
    [LoggerMessage(Level = LogLevel.Warning, Message = "Parcel {ParcelId} has no blob")]
    private static partial void LogMissingBlob(ILogger logger, Guid parcelId);
}
