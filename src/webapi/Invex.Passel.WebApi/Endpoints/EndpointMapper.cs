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
                .RequireAuthorization();

            group.MapPostPacket();
            group.MapGetPackets();
            group.MapGetPacketEvents();
            group.MapDeletePacket();
        }
    }
}
