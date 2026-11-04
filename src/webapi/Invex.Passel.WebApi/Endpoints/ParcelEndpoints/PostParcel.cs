using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

public static partial class ParcelEndpoints
{
    /// <summary>
    ///     An upload as the OpenAPI document describes it: a <c>multipart/form-data</c> body whose one part, named
    ///     <c>file</c>, carries the file and its name. The endpoint reads the body as a stream and never binds this.
    /// </summary>
    [PublicAPI]
    public sealed record ParcelUploadForm
    {
        public required IFormFile File { get; init; }
    }

    /// <summary>
    ///     The name of the part that carries the file.
    /// </summary>
    internal const string FilePartName = "file";

    /// <summary>
    ///     Room for the multipart framing around the file: the boundaries and the part's headers.
    /// </summary>
    internal const long MultipartOverheadBytes = 64 * 1024;

    /// <summary>
    ///     The request size limit the endpoint declares, which the configured file limit may not exceed: the
    ///     largest <see cref="ParcelOptions.MaxParcelBytes" /> allowed, and its framing. Each request is held to
    ///     the configured limit itself.
    /// </summary>
    internal const long MaxConfigurableRequestBytes = 1_073_741_824 + MultipartOverheadBytes;

    /// <summary>
    ///     The longest file name kept, after it is made safe: as long as a packet title.
    /// </summary>
    internal const int MaxFileNameLength = PacketEndpoints.PacketEndpoints.MaxTitleLength;

    /// <summary>
    ///     The longest boundary RFC 2046 allows.
    /// </summary>
    private const int MaxBoundaryLength = 70;

    private const int MaxPartHeadersLength = 16 * 1024;
    private const int MaxPartHeadersCount = 8;
    private const int CopyBufferSize = 81920;

    /// <summary>
    ///     How long an upload waits for a place on the instance before it is asked to come back.
    /// </summary>
    private static readonly TimeSpan UploadGateTimeout = TimeSpan.FromSeconds(5);

    private const string UploadGateRetryAfterSeconds = "5";

    /// <summary>
    ///     Where uploads are buffered while they are checked and stored. Each file deletes itself when closed.
    /// </summary>
    private static readonly string UploadDirectory = Path.Combine(Path.GetTempPath(), "passel-parcels");

    private const string SendAsFilePart = "Send the file as the file part of a multipart/form-data body.";

    /// <summary>
    ///     The multipart boundary of a request's <c>Content-Type</c>, or <c>null</c> when the body is not
    ///     <c>multipart/form-data</c> with a usable boundary.
    /// </summary>
    internal static string? Boundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return null;

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary);

        return boundary is { Length: > 0 and <= MaxBoundaryLength }
            ? boundary.ToString()
            : null;
    }

    public static void MapPostParcel(this IEndpointRouteBuilder builder) =>
        builder
            .MapPost(string.Empty, PostParcelAsync)
            .Accepts<ParcelUploadForm>("multipart/form-data")
            .Produces<ParcelResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            // Routing answers a body that is not multipart/form-data, as the Accepts metadata declares
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            // A ceiling for the server and the document; each request is narrowed to the configured file limit
            .WithMetadata(new RequestSizeLimitAttribute(MaxConfigurableRequestBytes))
            .RequireRateLimiting(RateLimitPolicies.ParcelUploads);

    // Streams one file in: the body is read as it arrives (never bound as a form), buffered to a temporary file
    // while it is counted and hashed, sniffed and thumbnailed, and stored. The parcel is recorded before its
    // bytes are read, reserving its size against the quota, and is marked deleted if the upload goes wrong at
    // any point after, so the sweep erases whatever of it was stored. (A plain comment: the OpenAPI document would
    // take an XML one for the summary of the operation.)
    private static async Task<IResult> PostParcelAsync(
        HttpContext httpContext,
        IIdEncodingService encodingService,
        IParcelStoreService parcelStoreService,
        IParcelQuotaService quotaService,
        ParcelUploadGate uploadGate,
        ParcelInspector inspector,
        IParcelStore store,
        IOptions<ParcelOptions> parcelOptions,
        IOptions<QuotaOptions> quotaOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (ownerId is not { Length: > 0 })
            return Results.Unauthorized();

        var options = parcelOptions.Value;
        var request = httpContext.Request;
        var maxRequestBytes = options.MaxParcelBytes + MultipartOverheadBytes;

        // Kestrel holds the body to this; the test server has no limit, and the count below holds there too
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
            sizeLimit.MaxRequestBodySize = maxRequestBytes;

        if (Boundary(request.ContentType) is not { } boundary)
            return FileProblem(SendAsFilePart);

        if (request.ContentLength > maxRequestBytes)
            return Refuse(ParcelQuota.TooLarge(options));

        // A body sent without a length may be as long as the largest file, and reserves that much
        var reserved = Math.Min(request.ContentLength ?? options.MaxParcelBytes, options.MaxParcelBytes);

        if (ParcelQuota.Exceeded(await quotaService.GetUsageAsync(ownerId, cancellationToken), reserved,
                quotaOptions.Value) is { } exceeded)
            return Refuse(exceeded);

        if (!await uploadGate.TryEnterAsync(UploadGateTimeout, cancellationToken))
        {
            httpContext.Response.Headers.RetryAfter = UploadGateRetryAfterSeconds;

            return Results.Problem("Too many files are being uploaded right now. Try again in a few seconds.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "File storage is busy");
        }

        try
        {
            var upload = new Upload(ownerId, reserved, options, quotaOptions.Value, parcelStoreService, quotaService,
                inspector, store, loggerFactory.CreateLogger(typeof(ParcelEndpoints)));

            return await upload.ReceiveAsync(new(boundary, request.Body)
                {
                    HeadersLengthLimit = MaxPartHeadersLength,
                    HeadersCountLimit = MaxPartHeadersCount,
                    // No limit of the reader's own: it would refuse a read that crosses the limit before the count
                    // saw the bytes, as a malformed body rather than a file too large. The count holds the file, and
                    // Kestrel the body as a whole
                    BodyLengthLimit = null,
                },
                encodingService,
                cancellationToken);
        }
        finally
        {
            uploadGate.Release();
        }
    }

    private static IResult FileProblem(string message) =>
        Problem("file", message);

    private static IResult Problem(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [key] = [message],
        });

    private static IResult Refuse(QuotaExceeded exceeded) =>
        Results.Problem(exceeded.Detail, statusCode: exceeded.StatusCode, title: exceeded.Title);

    /// <summary>
    ///     One upload from its first part on, holding what every step needs.
    /// </summary>
    private sealed class Upload(
        string ownerId,
        long reserved,
        ParcelOptions options,
        QuotaOptions quotas,
        IParcelStoreService parcelStoreService,
        IParcelQuotaService quotaService,
        ParcelInspector inspector,
        IParcelStore store,
        ILogger logger)
    {
        public async Task<IResult> ReceiveAsync(
            MultipartReader reader,
            IIdEncodingService encodingService,
            CancellationToken cancellationToken)
        {
            if (await TryReadSectionAsync(reader, cancellationToken) is not { } section ||
                !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.IsFileDisposition() ||
                !HeaderUtilities.RemoveQuotes(disposition.Name).Equals(FilePartName, StringComparison.Ordinal))
                return FileProblem(SendAsFilePart);

            var fileName = ParcelFileName.Sanitize(disposition.FileNameStar.HasValue
                ? disposition.FileNameStar.ToString()
                : HeaderUtilities.RemoveQuotes(disposition.FileName).ToString());

            if (fileName is { Length: > MaxFileNameLength })
                return Problem("fileName", $"FileName must be at most {MaxFileNameLength} characters.");

            // Recorded before a byte is read, so the reservation counts against uploads that follow at once
            var id = await parcelStoreService.BeginUploadAsync(ownerId, fileName, reserved, cancellationToken);

            try
            {
                var (parcel, refusal) = await StoreAsync(id, section, reader, cancellationToken);

                if (parcel is not null)
                    return Results.Created($"/api/parcels/{encodingService.Encode(parcel.Id)}",
                        ParcelResponse.From(parcel, encodingService));

                await FailAsync(id);

                return refusal!;
            }
            catch
            {
                // Not a finally: a stored upload must not be failed
                await FailAsync(id);

                throw;
            }
        }

        private async Task<(Parcel? Parcel, IResult? Refusal)> StoreAsync(
            Guid id,
            MultipartSection section,
            MultipartReader reader,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(UploadDirectory);

            await using var buffer = new FileStream(Path.Combine(UploadDirectory, Guid.NewGuid().ToString("N")),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            long size;
            string sha256;

            await using (var counted = new CountingHashStream(section.Body, options.MaxParcelBytes))
            {
                if (await CopyAsync(counted, buffer, cancellationToken) is { } refusal)
                    return (null, refusal);

                size = counted.BytesRead;
                sha256 = counted.GetSha256();
            }

            if (size == 0)
                return (null, FileProblem("The file is empty."));

            // Anything after the file is refused rather than ignored, so a client sending several learns it now
            if (await TryReadSectionAsync(reader, cancellationToken) is not null)
                return (null, FileProblem("Send one file per upload."));

            var inspection = await inspector.InspectAsync(buffer, cancellationToken);

            await using (inspection.Thumbnail)
            {
                if (inspection.Failure is { } failure)
                    LogNoThumbnail(logger, id, failure);

                buffer.Position = 0;
                await store.PutAsync(ParcelKeys.Original(id), buffer, inspection.ContentType, cancellationToken);

                if (inspection.Thumbnail is { } thumbnail)
                {
                    thumbnail.Position = 0;
                    await store.PutAsync(ParcelKeys.Thumbnail(id), thumbnail, ParcelSniffer.Webp, cancellationToken);
                }
            }

            // Again with the exact size, and without this upload's own reservation, now in the usage: only uploads
            // that reserved in the moments since the first check can tip it over
            var usage = await quotaService.GetUsageAsync(ownerId, cancellationToken);

            if (ParcelQuota.Exceeded(usage with
                {
                    Pending = usage.Pending - 1,
                    Bytes = usage.Bytes - reserved,
                },
                size,
                quotas) is { } exceeded)
                return (null, Refuse(exceeded));

            var parcel = await parcelStoreService.CompleteUploadAsync(id, inspection, size, sha256, cancellationToken);

            return parcel is null
                ? (null, Results.Problem("The upload took too long and was discarded. Upload the file again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Upload discarded"))
                : (parcel, null);
        }

        /// <summary>
        ///     Copies the file to the buffer. Returns the refusal of a file over the limit or a body that is not
        ///     multipart after all, and <c>null</c> once the file is copied.
        /// </summary>
        private async Task<IResult?> CopyAsync(Stream file, Stream buffer, CancellationToken cancellationToken)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

            try
            {
                while (true)
                {
                    int read;

                    // Only the reads are judged here: a failure to write the buffer is the server's own
                    try
                    {
                        read = await file.ReadAsync(chunk, cancellationToken);
                    }
                    catch (ParcelTooLargeException)
                    {
                        return Refuse(ParcelQuota.TooLarge(options));
                    }
                    catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    {
                        return Refuse(ParcelQuota.TooLarge(options));
                    }
                    catch (Exception exception) when (IsMalformed(exception, cancellationToken))
                    {
                        return FileProblem(SendAsFilePart);
                    }

                    if (read == 0)
                        return null;

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        /// <summary>
        ///     The next part of the body, or <c>null</c> when there is none or the body is not multipart after all.
        /// </summary>
        private static async Task<MultipartSection?> TryReadSectionAsync(
            MultipartReader reader,
            CancellationToken cancellationToken)
        {
            try
            {
                return await reader.ReadNextSectionAsync(cancellationToken);
            }
            catch (Exception exception) when (IsMalformed(exception, cancellationToken))
            {
                return null;
            }
        }

        /// <summary>
        ///     Whether reading the body failed because of what the client sent: the reader refuses framing it cannot
        ///     parse, part headers over their limits and a body that ends before its closing boundary. A client that
        ///     went away cancels the request instead, and that is not answered.
        /// </summary>
        private static bool IsMalformed(Exception exception, CancellationToken cancellationToken) =>
            exception is InvalidDataException or IOException && !cancellationToken.IsCancellationRequested;

        private async Task FailAsync(Guid id)
        {
            try
            {
                await parcelStoreService.FailUploadAsync(id);
            }
            catch (Exception exception)
            {
                // The upload is left Uploading, and the sweep deletes it once it is past the grace period
                LogFailUploadFailed(logger, exception, id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parcel {ParcelId} has no thumbnail: {Failure}")]
    private static partial void LogNoThumbnail(ILogger logger, Guid parcelId, string failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parcel {ParcelId} could not be marked failed; the sweep will expire it")]
    private static partial void LogFailUploadFailed(ILogger logger, Exception exception, Guid parcelId);
}
