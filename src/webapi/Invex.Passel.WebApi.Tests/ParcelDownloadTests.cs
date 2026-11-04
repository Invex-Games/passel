using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.ParcelEndpoints;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelDownloadTests
{
    private static Parcel Uploaded(string contentType, bool isImage, string? fileName) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "alice",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Status = ParcelStatus.Uploaded,
            Size = 10,
            ContentType = contentType,
            FileName = fileName,
            IsImage = isImage,
            Sha256 = new string('0', 64),
        };

    [Theory]
    [InlineData(ParcelSniffer.Png, true, true)]
    [InlineData(ParcelSniffer.Jpeg, true, true)]
    [InlineData(ParcelSniffer.Webp, true, true)]
    [InlineData(ParcelSniffer.Pdf, false, true)]
    // A PNG by its magic number that did not decode, an uploaded page and an uploaded SVG (both stored as
    // octet-stream) are all saved rather than shown
    [InlineData(ParcelSniffer.Png, false, false)]
    [InlineData(ParcelSniffer.OctetStream, false, false)]
    public void Only_a_decoded_image_or_a_PDF_is_shown_inline(string contentType, bool isImage, bool inline)
    {
        var parcel = Uploaded(contentType, isImage, "name");

        Assert.Equal(inline, ParcelDownload.ServesInline(parcel));
        Assert.StartsWith(inline ? "inline;" : "attachment;", ParcelDownload.Disposition(parcel));
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("drawing.svg")]
    [InlineData("photo.png")]
    public void The_name_never_makes_a_file_inline(string fileName) =>
        Assert.StartsWith("attachment;",
            ParcelDownload.Disposition(Uploaded(ParcelSniffer.OctetStream, false, fileName)));

    [Fact]
    public void A_plain_name_is_given_both_ways()
    {
        Assert.Equal("attachment; filename=report.txt; filename*=UTF-8''report.txt",
            ParcelDownload.Disposition(Uploaded(ParcelSniffer.OctetStream, false, "report.txt")));
    }

    [Fact]
    public void A_name_beyond_ascii_is_exact_in_filename_star_and_replaced_in_the_fallback()
    {
        var disposition = ParcelDownload.Disposition(Uploaded(ParcelSniffer.Png, true, "Café \"menu\" 🍕.png"));

        Assert.Equal("inline; filename=\"Caf_ _menu_ _.png\"; filename*=UTF-8''Caf%C3%A9%20%22menu%22%20%F0%9F%8D%95.png",
            disposition);
    }

    [Theory]
    [InlineData("a\\b.txt", "a_b.txt")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("naïve", "na_ve")]
    [InlineData("😀", "_")]
    public void The_fallback_keeps_printable_ascii_only(string name, string fallback) =>
        Assert.Equal(fallback, ParcelDownload.AsciiFallback(name));

    [Theory]
    [InlineData(ParcelSniffer.Png, true, "file.png")]
    [InlineData(ParcelSniffer.Jpeg, true, "file.jpg")]
    [InlineData(ParcelSniffer.Gif, true, "file.gif")]
    [InlineData(ParcelSniffer.Webp, true, "file.webp")]
    [InlineData(ParcelSniffer.Pdf, false, "file.pdf")]
    [InlineData(ParcelSniffer.OctetStream, false, "file")]
    public void A_file_without_a_name_is_named_by_its_type(string contentType, bool isImage, string name) =>
        Assert.EndsWith($"; filename={name}; filename*=UTF-8''{name}",
            ParcelDownload.Disposition(Uploaded(contentType, isImage, null)));
}
