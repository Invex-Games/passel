namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    public static void MapDeletePacket(this IEndpointRouteBuilder builder) =>
        builder
            .MapDelete("/{id}",
                async (
                    string id,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IPacketStoreService packetStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var packetId))
                        return Results.NotFound();

                    var deleted = await packetStoreService.Handle(new DeletePacket
                    {
                        UserId = ownerId,
                        PacketId = packetId,
                    });

                    return deleted
                        ? Results.NoContent()
                        : Results.NotFound();
                })
            // Deleting is as cheap as reading and bounded by what the user created, so it shares the read limit
            .RequireRateLimiting(RateLimitPolicies.Reads);
}
