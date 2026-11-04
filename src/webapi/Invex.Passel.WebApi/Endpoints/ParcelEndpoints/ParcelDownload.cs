using System.Text;
using Microsoft.Net.Http.Headers;

namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

/// <summary>
///     How a stored file is handed back: shown in the browser or saved, and under which name.
/// </summary>
internal static class ParcelDownload
{
    public const string Inline = "inline";
    public const string Attachment = "attachment";

    /// <summary>
    ///     Whether the browser may show the file itself: only an image that decoded, or a PDF. Anything else is
    ///     saved, whatever its name says, so an uploaded page or script is never opened on the API's origin.
    /// </summary>
    internal static bool ServesInline(Parcel parcel) =>
        parcel.IsImage || parcel.ContentType == ParcelSniffer.Pdf;

    /// <summary>
    ///     The <c>Content-Disposition</c> of the file's download: <see cref="Inline" /> or <see cref="Attachment" />
    ///     as <see cref="ServesInline" /> decides, under the uploaded name, or <c>file</c> and the extension of its
    ///     type when it has none.
    /// </summary>
    internal static string Disposition(Parcel parcel) =>
        Disposition(ServesInline(parcel) ? Inline : Attachment,
            parcel.FileName ?? $"file{Extension(parcel.ContentType)}");

    /// <summary>
    ///     A <c>Content-Disposition</c> naming <paramref name="name" /> twice: exactly, in UTF-8, as
    ///     <c>filename*</c>, and as an ASCII <c>filename</c> for the clients that read only that.
    /// </summary>
    internal static string Disposition(string dispositionType, string name) =>
        new ContentDispositionHeaderValue(dispositionType)
        {
            // The fallback first, as RFC 6266 advises, for the clients that take the first name they find
            FileName = AsciiFallback(name),
            FileNameStar = name,
        }.ToString();

    /// <summary>
    ///     <paramref name="name" /> with every character that is not printable ASCII, and the quote and backslash
    ///     that a quoted string would have to escape, replaced by <c>_</c>, one for each character as it is seen.
    /// </summary>
    internal static string AsciiFallback(string name)
    {
        var fallback = new StringBuilder(name.Length);

        foreach (var rune in name.EnumerateRunes())
            fallback.Append(rune.Value is >= 0x20 and < 0x7F and not '"' and not '\\'
                ? (char)rune.Value
                : '_');

        return fallback.ToString();
    }

    /// <summary>
    ///     The extension of a sniffed type, with its dot, or nothing for a type with no one extension.
    /// </summary>
    internal static string Extension(string contentType) =>
        contentType switch
        {
            ParcelSniffer.Png => ".png",
            ParcelSniffer.Jpeg => ".jpg",
            ParcelSniffer.Gif => ".gif",
            ParcelSniffer.Webp => ".webp",
            ParcelSniffer.Pdf => ".pdf",
            _ => string.Empty,
        };
}
