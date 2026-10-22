using System.Text;
using Microsoft.Extensions.Options;

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

    internal const int MaxTitleLength = 256;
    internal const int MaxUrlLength = 2048;
    internal const int MaxBodyLength = 64 * 1024;
    private const long MaxRequestBodySize = 256 * 1024;

    internal static Dictionary<string, string[]> Validate(PostPacketRequestBody packet)
    {
        var errors = new Dictionary<string, string[]>();

        switch (packet)
        {
            case PostPacketRequestBody.Link link:
                if (link.Name is { Length: > MaxTitleLength })
                    errors["name"] = [$"Name must be at most {MaxTitleLength} characters."];

                if (link.Url is not { Length: > 0 and <= MaxUrlLength } ||
                    !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    errors["url"] = [$"Url must be an absolute http or https URL of at most {MaxUrlLength} characters."];

                break;

            case PostPacketRequestBody.Text text:
                if (text.Title is { Length: > MaxTitleLength })
                    errors["title"] = [$"Title must be at most {MaxTitleLength} characters."];

                if (string.IsNullOrWhiteSpace(text.Body) || text.Body.Length > MaxBodyLength)
                    errors["body"] = [$"Body must be non-empty and at most {MaxBodyLength} characters."];

                break;
        }

        return errors;
    }

    /// <summary>
    ///     The content a packet adds to what the user has stored, in UTF-8 bytes. Close to the stored size of its
    ///     document, which is what the byte quota counts.
    /// </summary>
    internal static long ContentBytes(PostPacketRequestBody packet) =>
        packet switch
        {
            PostPacketRequestBody.Link link => Utf8Bytes(link.Name) + Utf8Bytes(link.Url),
            PostPacketRequestBody.Text text => Utf8Bytes(text.Title) + Utf8Bytes(text.Body),
            _ => 0,
        };

    private static long Utf8Bytes(string? value) =>
        value is null
            ? 0
            : Encoding.UTF8.GetByteCount(value);

    public static void MapPostPacket(this IEndpointRouteBuilder builder) =>
        builder
            .MapPost(string.Empty,
                async (
                    [FromBody] PostPacketRequestBody packet,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IPacketStoreService packetStoreService,
                    IPacketQuotaService quotaService,
                    IOptions<QuotaOptions> quotas) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (packet is PostPacketRequestBody.File)
                        return Results.Problem("File packets are not supported in the current version.",
                            statusCode: StatusCodes.Status400BadRequest);

                    var errors = Validate(packet);

                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);

                    var usage = await quotaService.GetUsageAsync(ownerId, httpContext.RequestAborted);

                    if (PacketQuota.Exceeded(usage, ContentBytes(packet), quotas.Value) is { } exceeded)
                        return Results.Problem(exceeded.Detail,
                            statusCode: exceeded.StatusCode,
                            title: exceeded.Title);

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

                    return result?.Packet switch
                    {
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
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodySize))
            .RequireRateLimiting(RateLimitPolicies.Creates);
}
