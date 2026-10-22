namespace Invex.Passel.WebApi.Endpoints;

internal static class EndpointMapper
{
    extension(IEndpointRouteBuilder builder)
    {
        public void MapPacketEndpoints()
        {
            var group = builder
                .NewVersionedApi("Packets")
                .MapGroup("/api/packets")
                .HasApiVersion(1)
                .RequireAuthorization()
                // Every packet endpoint answers 401 without a user and 429 over its rate limit; each declares the
                // responses of its own
                .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized))
                .ProducesProblem(StatusCodes.Status429TooManyRequests);

            group.MapPostPacket();
            group.MapGetPackets();
            group.MapGetPacketEvents();
            group.MapDeletePacket();
        }
    }
}
