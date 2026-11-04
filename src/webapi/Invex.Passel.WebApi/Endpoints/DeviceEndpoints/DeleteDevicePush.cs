namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    public static void MapDeleteDevicePush(this IEndpointRouteBuilder builder) =>
        builder
            .MapDelete("/{id}/push",
                async (
                    string id,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IDeviceStoreService deviceStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var deviceId))
                        return Results.NotFound();

                    // A device without a token is cleared already, so a repeated request succeeds too
                    var cleared = await deviceStoreService.Handle(new ClearDevicePush
                    {
                        UserId = ownerId,
                        DeviceId = deviceId,
                    });

                    return cleared
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireRateLimiting(RateLimitPolicies.DeviceWrites);
}
