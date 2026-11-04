namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    [PublicAPI]
    public sealed record RenameDeviceRequestBody
    {
        public required string Name { get; init; }
    }

    internal static Dictionary<string, string[]> Validate(RenameDeviceRequestBody device)
    {
        var errors = new Dictionary<string, string[]>();

        AddNameErrors(errors, device.Name);

        return errors;
    }

    public static void MapPatchDevice(this IEndpointRouteBuilder builder) =>
        builder
            .MapPatch("/{id}",
                async (
                    string id,
                    [FromBody] RenameDeviceRequestBody device,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IDeviceStoreService deviceStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var deviceId))
                        return Results.NotFound();

                    var errors = Validate(device);

                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);

                    var renamed = await deviceStoreService.Handle(new RenameDevice
                    {
                        UserId = ownerId,
                        DeviceId = deviceId,
                        Name = NormalizeName(device.Name),
                    });

                    return renamed is null
                        ? Results.NotFound()
                        : Results.Ok(DeviceResponse.From(renamed, encodingService));
                })
            .Produces<DeviceResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .AcceptsJson("Rename",
                new RenameDeviceRequestBody
                {
                    Name = "Work laptop",
                })
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodySize))
            .RequireRateLimiting(RateLimitPolicies.DeviceWrites);
}
