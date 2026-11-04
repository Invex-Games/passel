using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What an uploaded file turned out to be: its type sniffed from the bytes, whether it decoded as an image, its
///     dimensions as displayed, and a WebP thumbnail without metadata. When it is not an image, or not one that is
///     thumbnailed, <see cref="Failure" /> says why, for the caller to log.
/// </summary>
internal sealed record ParcelInspection(
    string ContentType,
    bool IsImage,
    int? Width,
    int? Height,
    MemoryStream? Thumbnail,
    string? Failure = null)
{
    public static ParcelInspection NotAnImage(string contentType, string? failure = null) =>
        new(contentType, false, null, null, null, failure);
}

/// <summary>
///     Sniffs an uploaded file's type and, for the raster images it allows, makes the thumbnail with SkiaSharp. The
///     image is identified before any pixel is decoded, so only an allowed format within the pixel and dimension
///     limits is decoded at all, and then at the smallest size the codec can manage (JPEG and WebP scale while
///     decoding; PNG and GIF decode at full size). The thumbnail is drawn upright from the EXIF orientation and
///     encoded with no colour profile, EXIF or XMP. The original is never changed.
/// </summary>
/// <remarks>
///     A decode is a native call that cannot be cancelled, so the pixel limit is what bounds its time and memory
///     (four bytes a pixel at full size), and decodes run one at a time on the instance. Any failure leaves the file
///     an ordinary download with no thumbnail; it never fails the upload.
/// </remarks>
internal sealed class ParcelInspector(IOptions<ParcelOptions> options)
{
    private const int WebpQuality = 80;

    // One decode at a time across the process: a PNG at the pixel limit takes 160 MB of native memory
    private static readonly SemaphoreSlim Decodes = new(1, 1);

    /// <summary>
    ///     Inspects <paramref name="file" />, which must be seekable, from its start. The stream is left open, at an
    ///     unspecified position.
    /// </summary>
    public async Task<ParcelInspection> InspectAsync(Stream file, CancellationToken cancellationToken)
    {
        var head = new byte[ParcelSniffer.HeadLength];
        file.Position = 0;
        var length = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        file.Position = 0;

        var sniffed = ParcelSniffer.Sniff(head.AsSpan(0, length));

        if (sniffed is null)
            return ParcelInspection.NotAnImage(ParcelSniffer.OctetStream);

        if (ExpectedFormat(sniffed) is not { } expected)
            return ParcelInspection.NotAnImage(sniffed);

        await Decodes.WaitAsync(cancellationToken);

        try
        {
            return Inspect(file, sniffed, expected, options.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // SkiaSharp throws for what its native code refuses, and the allocation of a bitmap can fail
            return ParcelInspection.NotAnImage(sniffed, $"threw {exception.GetType().Name}");
        }
        finally
        {
            Decodes.Release();
        }
    }

    private static ParcelInspection Inspect(
        Stream file,
        string sniffed,
        SKEncodedImageFormat expected,
        ParcelOptions options)
    {
        // Reads the header only; no pixel memory is allocated yet. The codec owns the Skia stream it reads, and the
        // Skia stream would close the file with it unless told not to; the upload stores the file afterwards
        using var codec = SKCodec.Create(new SKManagedStream(file, disposeManagedStream: false), out var created);

        if (codec is null)
            return ParcelInspection.NotAnImage(sniffed, $"not decodable: {created}");

        // Skia decodes more than the API allows (BMP, WBMP, ICO and what the platform adds), and a file whose bytes
        // say one format while its codec says another is not trusted as either
        if (codec.EncodedFormat != expected)
            return ParcelInspection.NotAnImage(sniffed, $"format {codec.EncodedFormat} where {expected} was sniffed");

        var width = codec.Info.Width;
        var height = codec.Info.Height;

        if ((long)width * height > options.MaxImagePixels ||
            width > options.MaxImageDimension ||
            height > options.MaxImageDimension)
            return ParcelInspection.NotAnImage(sniffed, $"too large to decode: {width}x{height}");

        var origin = codec.EncodedOrigin;

        using var decoded = Decode(codec, options.ThumbnailSize, out var decodeResult);

        // A truncated file decodes partly and says so; a part of an image is no image
        if (decoded is null)
            return ParcelInspection.NotAnImage(sniffed, $"decode: {decodeResult}");

        using var resized = Resize(decoded, options.ThumbnailSize);

        if (resized is null)
            return ParcelInspection.NotAnImage(sniffed, "resize failed");

        using var oriented = Orient(resized, origin);
        var thumbnail = EncodeWithoutMetadata(oriented);

        if (thumbnail is null)
            return ParcelInspection.NotAnImage(sniffed, "encode failed");

        var (displayedWidth, displayedHeight) = SwapsSides(origin)
            ? (height, width)
            : (width, height);

        return new(sniffed, true, displayedWidth, displayedHeight, thumbnail);
    }

    private static SKEncodedImageFormat? ExpectedFormat(string sniffed) =>
        sniffed switch
        {
            ParcelSniffer.Jpeg => SKEncodedImageFormat.Jpeg,
            ParcelSniffer.Png => SKEncodedImageFormat.Png,
            ParcelSniffer.Gif => SKEncodedImageFormat.Gif,
            ParcelSniffer.Webp => SKEncodedImageFormat.Webp,
            _ => null,
        };

    /// <summary>
    ///     The first frame (the only one of a still image), in sRGB, at the smallest size the codec offers that is
    ///     still at least <paramref name="size" /> on its longest side (or the full size, when that is smaller);
    ///     <c>null</c> unless it decoded completely.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, int size, out SKCodecResult result)
    {
        var longest = Math.Max(codec.Info.Width, codec.Info.Height);
        var target = Math.Min(size, longest);
        var scale = Math.Min(1f, (float)size / longest);
        var scaled = codec.GetScaledDimensions(scale);

        // A codec offers the supported scale nearest the one asked for, which may be smaller (JPEG decodes in
        // eighths: 2000 pixels asked to come down to 512 give 500), so the scale goes up an eighth at a time until
        // the decode is large enough for the thumbnail
        while (Math.Max(scaled.Width, scaled.Height) < target && scale < 1f)
        {
            scale = Math.Min(1f, scale + 0.125f);
            scaled = codec.GetScaledDimensions(scale);
        }

        result = TryDecode(codec, scaled, out var bitmap);

        // Codecs that cannot scale report their full size from GetScaledDimensions; should one refuse that size
        // anyway, it is decoded at full size, which the pixel limit allows
        if (result == SKCodecResult.InvalidScale)
            result = TryDecode(codec, codec.Info.Size, out bitmap);

        return bitmap;
    }

    private static SKCodecResult TryDecode(SKCodec codec, SKSizeI size, out SKBitmap? bitmap)
    {
        // The sRGB destination converts a Display P3 or other tagged source
        var info = new SKImageInfo(size.Width,
            size.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb());
        var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels(), new SKCodecOptions(0));

        if (result == SKCodecResult.Success)
        {
            bitmap = decoded;

            return result;
        }

        decoded.Dispose();
        bitmap = null;

        return result;
    }

    /// <summary>
    ///     A copy no larger than <paramref name="size" /> on its longest side; never enlarged.
    /// </summary>
    private static SKBitmap? Resize(SKBitmap bitmap, int size)
    {
        var k = Math.Min(1f, (float)size / Math.Max(bitmap.Width, bitmap.Height));

        if (k >= 1f)
            return bitmap.Copy();

        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * k)),
            Math.Max(1, (int)Math.Round(bitmap.Height * k)),
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            SKColorSpace.CreateSrgb());

        return bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>
    ///     The EXIF orientations 5 to 8 turn the image a quarter, so its sides swap when it is shown.
    /// </summary>
    internal static bool SwapsSides(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

    /// <summary>
    ///     <paramref name="source" /> drawn the way its EXIF orientation says it is shown.
    /// </summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft)
            return source.Copy();

        var info = SwapsSides(origin)
            ? source.Info.WithSize(source.Height, source.Width)
            : source.Info;

        var oriented = new SKBitmap(info);
        using var canvas = new SKCanvas(oriented);
        float w = source.Width, h = source.Height;

        switch (origin)
        {
            // Mirrored left to right
            case SKEncodedOrigin.TopRight:
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;

            // Turned half round
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;

            // Mirrored top to bottom
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;

            // Mirrored across the diagonal from the top left
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;

            // Turned a quarter clockwise
            case SKEncodedOrigin.RightTop:
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;

            // Mirrored across the diagonal from the top right
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(h, w);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;

            // Turned a quarter anticlockwise
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, w);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);

        return oriented;
    }

    /// <summary>
    ///     Lossy WebP with no chunk but the image: the pixels are encoded as if they had no colour space, which is what
    ///     keeps the ICC profile out (Skia never writes EXIF or XMP). <c>null</c> when the encoder fails.
    /// </summary>
    private static MemoryStream? EncodeWithoutMetadata(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels();
        using var stripped = new SKPixmap(pixmap.Info.WithColorSpace(null), pixmap.GetPixels(), pixmap.RowBytes);
        using var webp = stripped.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality));

        if (webp is null)
            return null;

        return new(webp.ToArray(), writable: false);
    }
}
