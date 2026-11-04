namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Decides a file's type from its first bytes. Only the types the API does something with are recognised: the
///     raster images it thumbnails and PDF, which it serves inline. Everything else, HTML and SVG included, is stored
///     and served as <c>application/octet-stream</c>; the type the client declares is never used.
/// </summary>
internal static class ParcelSniffer
{
    /// <summary>
    ///     How many leading bytes <see cref="Sniff" /> looks at. A PDF may start with junk before its header, which
    ///     readers accept within the first kilobyte.
    /// </summary>
    public const int HeadLength = 1024;

    public const string OctetStream = "application/octet-stream";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    public const string Pdf = "application/pdf";

    /// <summary>
    ///     The type <paramref name="head" /> starts like, or <c>null</c> for anything not recognised.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return Jpeg;

        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return Png;

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return Gif;

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return Webp;

        if (head[..Math.Min(head.Length, HeadLength)].IndexOf("%PDF-"u8) >= 0)
            return Pdf;

        return null;
    }
}
