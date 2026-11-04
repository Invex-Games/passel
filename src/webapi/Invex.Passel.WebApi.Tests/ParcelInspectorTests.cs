using System.Diagnostics;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using SkiaSharp;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelInspectorTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static Task<ParcelInspection> InspectAsync(byte[] bytes, ParcelOptions? options = null) =>
        new ParcelInspector(Microsoft.Extensions.Options.Options.Create(options ?? new ParcelOptions()))
            .InspectAsync(new MemoryStream(bytes), CancellationToken);

    private static SKBitmap DecodeThumbnail(ParcelInspection inspection)
    {
        Assert.NotNull(inspection.Thumbnail);

        return SKBitmap.Decode(inspection.Thumbnail.ToArray()) ??
               throw new InvalidOperationException("The thumbnail does not decode.");
    }

    /// <summary>
    ///     Which of the test colours a pixel is closest to, allowing for two rounds of lossy compression.
    /// </summary>
    private static string ColourOf(SKColor pixel) =>
        pixel switch
        {
            { Red: > 200, Green: > 200, Blue: > 200 } => "white",
            { Red: > 150, Green: < 90, Blue: < 90 } => "red",
            { Red: < 60, Green: < 60, Blue: < 60 } => "black",
            { Blue: > 150, Red: < 90, Green: < 90 } => "blue",
            _ => $"other {pixel}",
        };

    /// <summary>
    ///     The colour at the middle of each quarter of <paramref name="bitmap" />: top left, top right, bottom left,
    ///     bottom right.
    /// </summary>
    private static (string TopLeft, string TopRight, string BottomLeft, string BottomRight) Quarters(SKBitmap bitmap)
    {
        int left = bitmap.Width / 4, right = bitmap.Width * 3 / 4, top = bitmap.Height / 4, bottom = bitmap.Height * 3 / 4;

        return (ColourOf(bitmap.GetPixel(left, top)),
            ColourOf(bitmap.GetPixel(right, top)),
            ColourOf(bitmap.GetPixel(left, bottom)),
            ColourOf(bitmap.GetPixel(right, bottom)));
    }

    [Fact]
    public async Task A_png_gives_its_dimensions_and_a_webp_thumbnail_at_most_512_on_its_longest_side()
    {
        var inspection = await InspectAsync(TestImages.Png(1024, 600));

        Assert.Equal(ParcelSniffer.Png, inspection.ContentType);
        Assert.True(inspection.IsImage);
        Assert.Equal(1024, inspection.Width);
        Assert.Equal(600, inspection.Height);
        Assert.Null(inspection.Failure);

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal(["VP8 "], TestImages.RiffChunks(inspection.Thumbnail!.ToArray()));
        Assert.Equal(512, thumbnail.Width);
        Assert.Equal(300, thumbnail.Height);
        Assert.Equal(("white", "red", "black", "black"), Quarters(thumbnail));
    }

    [Fact]
    public async Task A_small_image_is_not_enlarged()
    {
        var inspection = await InspectAsync(TestImages.Webp(100, 50));

        Assert.Equal(ParcelSniffer.Webp, inspection.ContentType);
        Assert.True(inspection.IsImage);

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal((100, 50), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public async Task The_thumbnail_size_comes_from_the_options()
    {
        var inspection = await InspectAsync(TestImages.Jpeg(2000, 1000), new() { ThumbnailSize = 128 });

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal((128, 64), (thumbnail.Width, thumbnail.Height));
    }

    /// <summary>
    ///     The spec's case: a photo taken turned, with its location. The dimensions are as shown, the thumbnail is
    ///     upright, and it carries nothing but the image: no EXIF (so no location), no XMP and no colour profile.
    /// </summary>
    [Fact]
    public async Task A_turned_jpeg_with_a_location_gives_upright_dimensions_and_a_thumbnail_without_metadata()
    {
        var inspection = await InspectAsync(TestImages.JpegWithExif(2000, 1000, orientation: 6));

        Assert.Equal(ParcelSniffer.Jpeg, inspection.ContentType);
        Assert.True(inspection.IsImage);
        Assert.Equal(1000, inspection.Width);
        Assert.Equal(2000, inspection.Height);

        Assert.Equal(["VP8 "], TestImages.RiffChunks(inspection.Thumbnail!.ToArray()));

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal((256, 512), (thumbnail.Width, thumbnail.Height));

        // Turned a quarter clockwise: the white top-left quarter of the stored image is shown at the top right, and
        // the red top-right one at the bottom right
        Assert.Equal(("black", "white", "black", "red"), Quarters(thumbnail));
    }

    /// <summary>
    ///     The stored image has a white top-left quarter and a red top-right one. Each EXIF orientation puts them
    ///     somewhere else when the image is shown upright.
    /// </summary>
    [Theory]
    [InlineData(1, "white", "red", "black", "black")]
    [InlineData(2, "red", "white", "black", "black")]
    [InlineData(3, "black", "black", "red", "white")]
    [InlineData(4, "black", "black", "white", "red")]
    [InlineData(5, "white", "black", "red", "black")]
    [InlineData(6, "black", "white", "black", "red")]
    [InlineData(7, "black", "red", "black", "white")]
    [InlineData(8, "red", "black", "white", "black")]
    public async Task Every_exif_orientation_is_drawn_upright(
        ushort orientation,
        string topLeft,
        string topRight,
        string bottomLeft,
        string bottomRight)
    {
        var inspection = await InspectAsync(TestImages.JpegWithExif(160, 80, orientation, withGps: false));

        var swapped = orientation >= 5;

        Assert.True(inspection.IsImage);
        Assert.Equal(swapped ? (80, 160) : (160, 80), (inspection.Width, inspection.Height));

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal((inspection.Width, inspection.Height), (thumbnail.Width, thumbnail.Height));
        Assert.Equal((topLeft, topRight, bottomLeft, bottomRight), Quarters(thumbnail));
    }

    [Fact]
    public async Task An_animated_gif_is_thumbnailed_from_its_first_frame()
    {
        var inspection = await InspectAsync(TestImages.AnimatedGif(frames: 2));

        Assert.Equal(ParcelSniffer.Gif, inspection.ContentType);
        Assert.True(inspection.IsImage);
        Assert.Equal((4, 3), (inspection.Width, inspection.Height));

        using var thumbnail = DecodeThumbnail(inspection);

        Assert.Equal(("red", "red", "red", "red"), Quarters(thumbnail));
    }

    /// <summary>
    ///     A file a few dozen bytes long that would take 40 GB to decode. It is refused from its header, before any
    ///     pixel memory is allocated, so it returns at once; a decode would not.
    /// </summary>
    [Fact]
    public async Task An_image_claiming_more_pixels_than_allowed_is_not_decoded()
    {
        var started = Stopwatch.GetTimestamp();

        var inspection = await InspectAsync(TestImages.PngClaiming(100_000, 100_000));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Assert.Equal(ParcelSniffer.Png, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
        Assert.Null(inspection.Width);
        Assert.Contains("too large", inspection.Failure);
    }

    [Theory]
    [InlineData(17_000, 10)]
    [InlineData(10, 17_000)]
    public async Task An_image_with_a_side_over_the_limit_is_not_decoded(int width, int height)
    {
        var inspection = await InspectAsync(TestImages.PngClaiming(width, height));

        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
        Assert.Contains("too large", inspection.Failure);
    }

    [Fact]
    public async Task The_pixel_limit_comes_from_the_options()
    {
        var inspection = await InspectAsync(TestImages.Png(100, 100), new() { MaxImagePixels = 9_999 });

        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
    }

    [Fact]
    public async Task A_truncated_jpeg_is_not_an_image()
    {
        var jpeg = TestImages.Jpeg(800, 400);

        var inspection = await InspectAsync(jpeg[..(jpeg.Length / 2)]);

        Assert.Equal(ParcelSniffer.Jpeg, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
        Assert.NotNull(inspection.Failure);
    }

    [Fact]
    public async Task A_file_that_starts_like_a_png_and_is_not_one_is_not_an_image()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .."not really a png"u8];

        var inspection = await InspectAsync(bytes);

        Assert.Equal(ParcelSniffer.Png, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
    }

    /// <summary>
    ///     Skia decodes BMP too, but the API thumbnails only the formats it sniffs; anything else is a plain file.
    /// </summary>
    [Fact]
    public async Task A_format_skia_decodes_but_the_api_does_not_allow_is_a_plain_file()
    {
        // A 1 by 1 24-bit BMP
        var bmp = new byte[58];
        "BM"u8.CopyTo(bmp);
        BitConverter.GetBytes(58).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(1).CopyTo(bmp, 18);
        BitConverter.GetBytes(1).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(4).CopyTo(bmp, 34);

        var inspection = await InspectAsync(bmp);

        Assert.Equal(ParcelSniffer.OctetStream, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
    }

    [Theory]
    [InlineData("<!doctype html><script>alert(1)</script>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("just some text")]
    public async Task Html_svg_and_text_are_octet_streams(string content)
    {
        var inspection = await InspectAsync(System.Text.Encoding.UTF8.GetBytes(content));

        Assert.Equal(ParcelSniffer.OctetStream, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
    }

    [Fact]
    public async Task A_pdf_is_a_pdf_without_a_thumbnail()
    {
        var inspection = await InspectAsync("%PDF-1.7\n%âã\n1 0 obj\n"u8.ToArray());

        Assert.Equal(ParcelSniffer.Pdf, inspection.ContentType);
        Assert.False(inspection.IsImage);
        Assert.Null(inspection.Thumbnail);
        Assert.Null(inspection.Failure);
    }

    [Fact]
    public async Task An_empty_file_is_an_octet_stream()
    {
        var inspection = await InspectAsync([]);

        Assert.Equal(ParcelSniffer.OctetStream, inspection.ContentType);
        Assert.False(inspection.IsImage);
    }

    /// <summary>
    ///     The upload stores the file from the same stream after inspecting it, so the inspector must leave it open.
    /// </summary>
    [Fact]
    public async Task The_file_is_left_open_to_be_stored()
    {
        var bytes = TestImages.Jpeg(300, 200);
        var file = new MemoryStream(bytes);

        await new ParcelInspector(Microsoft.Extensions.Options.Options.Create(new ParcelOptions()))
            .InspectAsync(file, CancellationToken);

        file.Position = 0;
        Assert.Equal(bytes, file.ToArray());
        Assert.True(file.CanRead);
    }
}
