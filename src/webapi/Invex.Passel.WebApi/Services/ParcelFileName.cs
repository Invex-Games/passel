using System.Globalization;
using System.Text;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Makes the file name a client gives safe to store and to send back in a <c>Content-Disposition</c> header.
/// </summary>
internal static class ParcelFileName
{
    /// <summary>
    ///     The last segment of <paramref name="fileName" /> without control characters, line and paragraph separators
    ///     or bidirectional controls (which can make <c>gpj.exe</c> display as <c>exe.jpg</c>), in Unicode
    ///     normalisation form C, with leading and trailing spaces and dots trimmed as browsers trim them when saving.
    ///     <c>null</c> when nothing is left. The length is not limited here; the caller decides what is too long.
    /// </summary>
    public static string? Sanitize(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var kept = new StringBuilder(fileName.Length);

        // By code point, so the halves of a pair are judged together; a lone half comes out as the replacement
        // character, which is dropped, as normalisation would refuse it
        foreach (var rune in fileName.EnumerateRunes())
            if (!IsRemoved(rune))
                kept.Append(rune.ToString());

        // Clients send paths (C:\Users\me\Pictures\a.png from old browsers); only the name is the user's to keep
        var name = kept.ToString();
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

        name = name.Normalize(NormalizationForm.FormC);

        var start = 0;
        var end = name.Length;

        while (start < end && IsTrimmed(name[start]))
            start++;

        while (end > start && IsTrimmed(name[end - 1]))
            end--;

        return end > start
            ? name[start..end]
            : null;
    }

    private static bool IsTrimmed(char c) =>
        c == '.' || char.IsWhiteSpace(c);

    private static bool IsRemoved(Rune rune) =>
        rune == Rune.ReplacementChar ||
        Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => true,

            // The bidirectional marks, embeddings, overrides and isolates, and the byte order mark. Other format
            // characters, such as the joiners inside emoji, stay
            UnicodeCategory.Format => rune.Value is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E)
                or (>= 0x2066 and <= 0x2069) or 0xFEFF,

            _ => false,
        };
}
