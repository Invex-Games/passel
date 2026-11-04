namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    /// <summary>
    ///     All of the caller's devices, in the order they registered. Not paged: the device cap bounds the list.
    /// </summary>
    [PublicAPI]
    public sealed record GetDevicesResponseBody
    {
        public required IReadOnlyList<DeviceResponse> Devices { get; init; }
    }

    public static void MapGetDevices(this IEndpointRouteBuilder builder) =>
        builder
            .MapGet(string.Empty,
                async (
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IQuerySession querySession) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    // Served by the leading member of the unique index on the user and the installation id
                    var devices = await querySession
                        .Query<Device>()
                        .Where(d => d.UserId == ownerId)
                        .OrderBy(d => d.Id)
                        .ToListAsync(httpContext.RequestAborted);

                    return Results.Ok(new GetDevicesResponseBody
                    {
                        Devices = devices
                            .Select(d => DeviceResponse.From(d, encodingService))
                            .ToList(),
                    });
                })
            .Produces<GetDevicesResponseBody>()
            .RequireRateLimiting(RateLimitPolicies.Reads);
}
