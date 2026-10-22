namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    /// <summary>
    ///     A packet as the API returns it: the shape of a <c>GET /api/packets</c> item, of the
    ///     <c>POST /api/packets</c> response and of the <c>packet-created</c> event data.
    /// </summary>
    /// <remarks>
    ///     Declared once and built from the stored document by <see cref="From" /> alone. The request body
    ///     (<see cref="PostPacketRequestBody" />) stays separate: a packet has no id and no creation time before it is
    ///     stored. The contract is documented in <c>docs/reference/http-api.md</c>, generated into
    ///     <c>contracts/openapi.json</c> and sampled in <c>contracts/fixtures/</c>; the integration tests fail when
    ///     either falls behind this type.
    /// </remarks>
    [PublicAPI]
    [Union]
    [JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
    [JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
    [JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
    public partial record PacketResponse
    {
        /// <summary>
        ///     The packet's public id, opaque to clients; the list pages on it.
        /// </summary>
        // Ordered ahead of each kind's own fields, so the JSON reads as the documentation does: "$type", id,
        // createdAt, then the content
        [JsonPropertyOrder(-2)]
        public required string Id { get; init; }

        /// <summary>
        ///     When the packet was created, in UTC.
        /// </summary>
        [JsonPropertyOrder(-1)]
        public required DateTimeOffset CreatedAt { get; init; }

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

        /// <summary>
        ///     The one mapping from a stored packet to the wire. The ids are public ids, never the stored Guids.
        /// </summary>
        public static PacketResponse From(Packet packet, IIdEncodingService encodingService) =>
            packet.Content switch
            {
                PacketContent.Link link => new Link
                {
                    Id = encodingService.Encode(packet.Id),
                    CreatedAt = packet.CreatedAt,
                    Name = link.Name,
                    Url = link.Url,
                },
                PacketContent.Text text => new Text
                {
                    Id = encodingService.Encode(packet.Id),
                    CreatedAt = packet.CreatedAt,
                    Title = text.Title,
                    Body = text.Body,
                },
                PacketContent.File file => new File
                {
                    Id = encodingService.Encode(packet.Id),
                    CreatedAt = packet.CreatedAt,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    ParcelId = encodingService.Encode(file.ParcelId),
                    IsImage = file.IsImage,
                },
                _ => throw new UnreachableException(),
            };
    }
}
