namespace Invex.Passel.WebApi.Endpoints.PacketEndpoints;

public static partial class PacketEndpoints
{
    internal const int GetPacketsBatchSize = 50;

    /// <summary>
    ///     One page of the caller's packets, newest first.
    /// </summary>
    [PublicAPI]
    public sealed partial record GetPacketsResponseBody
    {
        public required IReadOnlyList<PacketResponse> Packets { get; init; }

        /// <summary>
        ///     The cursor for the next page, or <c>null</c> on the last one.
        /// </summary>
        public string? NextCursor { get; init; }
    }

    /// <summary>
    ///     Splits a result set fetched with <c>Take(batchSize + 1)</c> into the page to return and the cursor for the
    ///     next page. The extra row only signals that more data exists; the cursor is the last row actually returned.
    /// </summary>
    internal static (IReadOnlyList<Packet> Page, Guid? NextCursor) Paginate(
        IReadOnlyList<Packet> fetched,
        int batchSize)
    {
        if (fetched.Count <= batchSize)
            return (fetched, null);

        var page = fetched
            .Take(batchSize)
            .ToList();

        return (page, page[^1].Id);
    }

    public static void MapGetPackets(this IEndpointRouteBuilder builder) =>
        builder
            .MapGet(string.Empty,
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
                        .Query<Packet>()
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
                            .Select(p => PacketResponse.From(p, encodingService))
                            .ToList(),
                        NextCursor = nextCursor is { } next
                            ? encodingService.Encode(next)
                            : null,
                    });
                })
            .Produces<GetPacketsResponseBody>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireRateLimiting(RateLimitPolicies.Reads);
}
