namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

public static partial class ParcelEndpoints
{
    public static void MapDeleteParcel(this IEndpointRouteBuilder builder) =>
        builder
            .MapDelete("/{id}",
                async (
                    string id,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IParcelStoreService parcelStoreService,
                    CancellationToken cancellationToken) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var parcelId))
                        return Results.NotFound();

                    // The file is unavailable at once; its bytes are erased by the sweep, which this wakes
                    var result = await parcelStoreService.Handle(new DeleteParcel
                        {
                            UserId = ownerId,
                            ParcelId = parcelId,
                        },
                        cancellationToken);

                    return result switch
                    {
                        DeleteParcelResult.Deleted => Results.NoContent(),
                        DeleteParcelResult.NotFound => Results.NotFound(),
                        DeleteParcelResult.Attached => Results.Problem(
                            "This file has been sent; delete the item it is in.",
                            statusCode: StatusCodes.Status409Conflict,
                            title: "Delete the item instead"),
                        _ => throw new UnreachableException(),
                    };
                })
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            // A discard undoes an upload, so it counts against the same limit
            .RequireRateLimiting(RateLimitPolicies.ParcelUploads);
}
