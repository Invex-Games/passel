namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    public static void MapDeleteDevice(this IEndpointRouteBuilder builder) =>
        builder
            .MapDelete("/{id}",
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

                    var removed = await deviceStoreService.Handle(new RemoveDevice
                    {
                        UserId = ownerId,
                        DeviceId = deviceId,
                    });

                    return removed
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireRateLimiting(RateLimitPolicies.DeviceWrites);
}
