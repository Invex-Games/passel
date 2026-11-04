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

        /// <summary>
        ///     Sends a file uploaded to <c>POST /api/parcels</c>. Its type and whether it is an image come from the
        ///     upload; only its name may be changed.
        /// </summary>
        [PublicAPI]
        public sealed partial record File
        {
            /// <summary>
            ///     The <c>parcelId</c> the upload answered with.
            /// </summary>
            public required string ParcelId { get; init; }

            /// <summary>
            ///     The name to send the file under, or <c>null</c> to keep the one it was uploaded with.
            /// </summary>
            public string? FileName { get; init; }
        }
    }

    internal const int MaxTitleLength = 256;
    internal const int MaxUrlLength = 2048;
    internal const int MaxBodyLength = 64 * 1024;
    private const long MaxRequestBodySize = 256 * 1024;

    /// <summary>
    ///     Longer than any id the API hands out, so a longer one is refused before it is decoded.
    /// </summary>
    internal const int MaxParcelIdLength = 64;

    /// <summary>
    ///     What a File packet counts against the byte quota on top of its name: the type and the parcel id it stores.
    ///     The file's own bytes count against the file quota, at upload.
    /// </summary>
    internal const long FileContentBytes = 64;

    internal const string NoSuchParcelMessage = "ParcelId must be the id of a file you uploaded.";

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

            case PostPacketRequestBody.File file:
                if (file.ParcelId is not { Length: > 0 and <= MaxParcelIdLength })
                    errors["parcelId"] = [NoSuchParcelMessage];

                if (file.FileName is { Length: > MaxTitleLength })
                    errors["fileName"] = [$"FileName must be at most {MaxTitleLength} characters."];

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
            PostPacketRequestBody.File file => Utf8Bytes(file.FileName) + FileContentBytes,
            _ => throw new UnreachableException(),
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

                    var errors = Validate(packet);

                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);

                    // An id that decodes to nothing is answered as one that names no file of the user's
                    var parcelId = Guid.Empty;

                    if (packet is PostPacketRequestBody.File { ParcelId: var encodedParcelId } &&
                        !encodingService.TryDecode(encodedParcelId, out parcelId))
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["parcelId"] = [NoSuchParcelMessage],
                        });

                    var usage = await quotaService.GetUsageAsync(ownerId, httpContext.RequestAborted);

                    if (PacketQuota.Exceeded(usage, ContentBytes(packet), quotas.Value) is { } exceeded)
                        return Results.Problem(exceeded.Detail,
                            statusCode: exceeded.StatusCode,
                            title: exceeded.Title);

                    // A file is sent once: the packet and the file's attachment to it are one commit
                    if (packet is PostPacketRequestBody.File file)
                    {
                        var result = await packetStoreService.Handle(new CreateFilePacket
                        {
                            UserId = ownerId,
                            ParcelId = parcelId,
                            FileName = ParcelFileName.Sanitize(file.FileName),
                        });

                        return result switch
                        {
                            CreateFilePacketResult.Created { Packet: var filePacket } =>
                                Results.Ok(PacketResponse.From(filePacket, encodingService)),
                            CreateFilePacketResult.ParcelNotFound => Results.ValidationProblem(
                                new Dictionary<string, string[]>
                                {
                                    ["parcelId"] =
                                        ["No such file to send: it may have expired or been deleted. Upload it again."],
                                }),
                            CreateFilePacketResult.AlreadyAttached => Results.Problem(
                                "This file is already in another item; upload it again to send it twice.",
                                statusCode: StatusCodes.Status409Conflict,
                                title: "File already sent"),
                            _ => throw new UnreachableException(),
                        };
                    }

                    var created = packet switch
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
                        _ => throw new UnreachableException(),
                    };

                    // The created packet comes back in the list-item shape; From returns the union, so the "$type"
                    // discriminator is written
                    return Results.Ok(PacketResponse.From(created, encodingService));
                })
            .Produces<PacketResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
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
                    ParcelId = "qrjMmYBVnK8m3coMo0hiMeN",
                    FileName = "screenshot.png",
                })
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodySize))
            .RequireRateLimiting(RateLimitPolicies.Creates);
}
