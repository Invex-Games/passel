using System.Text;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelSnifferTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, ParcelSniffer.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, ParcelSniffer.Png)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x01, 0x00 }, ParcelSniffer.Gif)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00 }, ParcelSniffer.Gif)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20 },
        ParcelSniffer.Webp)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A }, ParcelSniffer.Pdf)]
    public void Each_type_is_known_by_its_first_bytes(byte[] head, string expected)
    {
        Assert.Equal(expected, ParcelSniffer.Sniff(head));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(ParcelSniffer.HeadLength - 5)]
    public void A_pdf_header_after_junk_within_the_first_kilobyte_is_a_pdf(int offset)
    {
        var head = new byte[offset + 9];
        "%PDF-1.4\n"u8.CopyTo(head.AsSpan(offset));

        Assert.Equal(ParcelSniffer.Pdf, ParcelSniffer.Sniff(head));
    }

    [Fact]
    public void A_pdf_header_past_the_first_kilobyte_is_not_looked_for()
    {
        var head = new byte[ParcelSniffer.HeadLength + 9];
        "%PDF-1.4\n"u8.CopyTo(head.AsSpan(ParcelSniffer.HeadLength - 4));

        Assert.Null(ParcelSniffer.Sniff(head));
    }

    [Theory]
    [InlineData("<!doctype html><html><body>hi</body></html>")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("plain text")]
    [InlineData("RIFF\u0010\0\0\0WAVEfmt ")]
    [InlineData("GIF90a")]
    public void Anything_else_is_not_recognised(string content)
    {
        Assert.Null(ParcelSniffer.Sniff(Encoding.UTF8.GetBytes(content)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42 })]
    public void A_head_too_short_for_any_signature_is_not_recognised(byte[] head)
    {
        Assert.Null(ParcelSniffer.Sniff(head));
    }
}
