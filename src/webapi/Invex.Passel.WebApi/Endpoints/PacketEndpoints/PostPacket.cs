namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    [PublicAPI]
    [Union]
    [JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
    [JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
    [JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
    public partial record PostPacketRequestBody
    {
        [PublicAPI]
        public sealed partial record Link
        {
            public required string? Name { get; init; }

            public required string Url { get; init; }
        }

        [PublicAPI]
        public sealed partial record Text
        {
            public required string? Title { get; init; }

            public required string Body { get; init; }
        }

        [PublicAPI]
        public sealed partial record File
        {
            public required string? FileName { get; init; }

            public required string ContentType { get; init; }

            public required object Payload { get; init; }
        }
    }

    [PublicAPI]
    [Union]
    [JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
    [JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
    [JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
    public partial record PostPacketResponseBody
    {
        public required string Id { get; init; }

        [PublicAPI]
        public sealed partial record Link
        {
            public required string? Name { get; init; }

            public required string Url { get; init; }
        }

        [PublicAPI]
        public sealed partial record Text
        {
            public required string? Title { get; init; }

            public required string Body { get; init; }
        }

        [PublicAPI]
        public sealed partial record File
        {
            public required string? FileName { get; init; }

            public required string ContentType { get; init; }

            public required string ParcelId { get; init; }

            public required bool IsImage { get; init; }
        }
    }

    public static void MapPostPacket(this IEndpointRouteBuilder builder) =>
        builder
            .MapPost(string.Empty,
                async (
                    [FromBody] PostPacketRequestBody packet,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IPacketStoreService packetStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

                    if (ownerId is not { Length: > 0 })
                        return Results.Forbid();

                    var result = packet switch
                    {
                        PostPacketRequestBody.Text text => await packetStoreService.Handle(new CreateTextPacket
                        {
                            UserId = ownerId,
                            Title = text.Title,
                            Body = text.Body,
                        }),
                        PostPacketRequestBody.Link link => await packetStoreService.Handle(new CreateLinkPacket
                        {
                            UserId = ownerId,
                            Title = link.Name,
                            Url = link.Url,
                        }),
                        _ => null,
                    };

                    if (result is null && packet is PostPacketRequestBody.File)
                        return Results.Problem("File packets are not supported in the current version.",
                            statusCode: StatusCodes.Status400BadRequest);

                    return result?.Packet switch
                    {
                        Packet.File => throw new NotImplementedException(),
                        Packet.Link linkResult => Results.Ok(new PostPacketResponseBody.Link
                        {
                            Name = linkResult.Name,
                            Url = linkResult.Url,
                            Id = encodingService.Encode(result.Id),
                        }),
                        Packet.Text textResult => Results.Ok(new PostPacketResponseBody.Text
                        {
                            Title = textResult.Title,
                            Body = textResult.Body,
                            Id = encodingService.Encode(result.Id),
                        }),
                        null => Results.Problem("Failed to create packet.",
                            statusCode: StatusCodes.Status500InternalServerError),
                        _ => throw new UnreachableException(),
                    };
                })
            .AcceptsJson(nameof(PostPacketRequestBody.Link),
                new PostPacketRequestBody.Link
                {
                    Name = "Packet name",
                    Url = "https://example.com",
                })
            .AcceptsJson(nameof(PostPacketRequestBody.Text),
                new PostPacketRequestBody.Text
                {
                    Title = "Packet title",
                    Body = "Packet body"
                })
            .AcceptsJson(nameof(PostPacketRequestBody.File),
                new PostPacketRequestBody.File
                {
                    FileName = "example.txt",
                    ContentType = "text/plain",
                    Payload = "SGVsbG8sIFdvcmxkIQ==", // "Hello, World!" in Base64
                });
}
