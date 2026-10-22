namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    internal const int GetPacketsBatchSize = 50;

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

    /// <summary>
    ///     Splits a result set fetched with <c>Take(batchSize + 1)</c> into the page to return and the cursor for the
    ///     next page. The extra row only signals that more data exists; the cursor is the last row actually returned.
    /// </summary>
    internal static (IReadOnlyList<PacketAggregate> Page, Guid? NextCursor) Paginate(
        IReadOnlyList<PacketAggregate> fetched,
        int batchSize)
    {
        if (fetched.Count <= batchSize)
            return (fetched, null);

        var page = fetched
            .Take(batchSize)
            .ToList();

        return (page, page[^1].Id);
    }

    internal static GetPacketsResponseBody.Packet ToResponsePacket(
        PacketAggregate aggregate,
        IIdEncodingService encodingService) =>
        aggregate.Packet switch
        {
            Packet.Link link => new GetPacketsResponseBody.Packet.Link
            {
                Name = link.Name,
                Url = link.Url,
                Id = encodingService.Encode(aggregate.Id),
            },
            Packet.Text text => new GetPacketsResponseBody.Packet.Text
            {
                Title = text.Title,
                Body = text.Body,
                Id = encodingService.Encode(aggregate.Id),
            },
            Packet.File file => new GetPacketsResponseBody.Packet.File
            {
                FileName = file.FileName,
                ContentType = file.ContentType,
                ParcelId = encodingService.Encode(file.ParcelId),
                IsImage = file.IsImage,
                Id = encodingService.Encode(aggregate.Id),
            },
            _ => throw new UnreachableException(),
        };

    public static void MapGetPackets(this IEndpointRouteBuilder builder) =>
        builder.MapGet(string.Empty,
            async (
                [FromQuery] string? cursor,
                HttpContext httpContext,
                IIdEncodingService encodingService,
                IQuerySession querySession) =>
            {
                var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (ownerId is not { Length: > 0 })
                    return Results.Unauthorized();

                Guid? cursorId = null;

                if (cursor is not null)
                {
                    if (!encodingService.TryDecode(cursor, out var decodedCursor))
                        return Results.Problem("The cursor is invalid.", statusCode: StatusCodes.Status400BadRequest);

                    cursorId = decodedCursor;
                }

                var query = querySession
                    .Query<PacketAggregate>()
                    .Where(p => p.UserId == ownerId);

                if (cursorId is { } beforeId)
                    query = query.Where(p => p.Id < beforeId);

                var fetched = await query
                    .OrderByDescending(p => p.Id)
                    .Take(GetPacketsBatchSize + 1)
                    .ToListAsync();

                var (page, nextCursor) = Paginate(fetched, GetPacketsBatchSize);

                return Results.Ok(new GetPacketsResponseBody
                {
                    Packets = page
                        .Select(p => ToResponsePacket(p, encodingService))
                        .ToList(),
                    NextCursor = nextCursor is { } next
                        ? encodingService.Encode(next)
                        : null,
                });
            });
}
