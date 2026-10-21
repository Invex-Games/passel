namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    [PublicAPI]
    public sealed partial record GetPacketsResponseBody
    {
        public required IReadOnlyList<Packet> Packets { get; init; }

        public string? NextCursor { get; init; }

        [PublicAPI]
        [Union]
        [JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
        [JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
        [JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
        public partial record Packet
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
    }

    public static void MapGetPackets(this IEndpointRouteBuilder builder) =>
        builder.MapGet(string.Empty,
            async ([FromQuery] string? cursor, IIdEncodingService encodingService, IQuerySession querySession) =>
            {
                const int batchSize = 50;

                var packets = cursor is null
                    ? await querySession
                        .Query<PacketAggregate>()
                        .OrderByDescending(p => p.Id)
                        .Take(batchSize + 1)
                        .ToListAsync()
                    : await querySession
                        .Query<PacketAggregate>()
                        .Where(x => x.Id < encodingService.Decode(cursor))
                        .OrderByDescending(p => p.Id)
                        .Take(batchSize + 1)
                        .ToListAsync();

                if (packets.Count is 0)
                    return Results.Ok(new GetPacketsResponseBody
                    {
                        Packets = [],
                        NextCursor = null,
                    });

                var nextCursor = packets.Count >= batchSize
                    ? packets.Last()
                        .Id
                    : Guid.Empty;

                return Results.Ok(new GetPacketsResponseBody
                {
                    Packets = packets
                        .Take(batchSize)
                        .Select<PacketAggregate, GetPacketsResponseBody.Packet>(p => p.Packet switch
                        {
                            Packet.Link link => new GetPacketsResponseBody.Packet.Link
                            {
                                Name = link.Name,
                                Url = link.Url,
                                Id = encodingService.Encode(p.Id),
                            },
                            Packet.Text text => new GetPacketsResponseBody.Packet.Text
                            {
                                Title = text.Title,
                                Body = text.Body,
                                Id = encodingService.Encode(p.Id),
                            },
                            Packet.File file => new GetPacketsResponseBody.Packet.File
                            {
                                FileName = file.FileName,
                                ContentType = file.ContentType,
                                ParcelId = encodingService.Encode(file.ParcelId),
                                IsImage = file.IsImage,
                                Id = encodingService.Encode(p.Id),
                            },
                        })
                        .ToList(),
                    NextCursor = nextCursor != Guid.Empty
                        ? encodingService.Encode(nextCursor)
                        : null,
                });
            });
}
