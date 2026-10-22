using System.Net;
using System.Text.RegularExpressions;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Reads a preview out of a page's HTML: the Open Graph tags first, the Twitter card tags next, and the plain
///     <c>&lt;title&gt;</c> and <c>&lt;meta name="description"&gt;</c> last. Only the head is read, with comments,
///     scripts and styles taken out first so a tag quoted inside one does not count.
/// </summary>
internal static partial class LinkPreviewExtractor
{
    /// <summary>
    ///     The preview the page describes, or <c>null</c> when it has no title, description or image to show.
    /// </summary>
    public static LinkPreview? Extract(string html, Uri pageUrl, DateTimeOffset fetchedAt)
    {
        string? ogTitle = null, twitterTitle = null;
        string? ogDescription = null, twitterDescription = null, metaDescription = null;
        string? ogImage = null, twitterImage = null;
        string? documentTitle = null;

        try
        {
            var head = Clutter().Replace(HeadOf(html), string.Empty);

            foreach (Match meta in MetaTag().Matches(head))
            {
                var attributes = Attributes(meta.Groups[1].Value);

                // Open Graph tags use "property", the others "name"; some pages mix them up, so either counts
                var key = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name");

                if (key is null || !attributes.TryGetValue("content", out var content))
                    continue;

                // The first tag of each kind wins: the first og:image is the main one
                switch (key.Trim().ToLowerInvariant())
                {
                    case "og:title":
                        ogTitle ??= content;

                        break;
                    case "twitter:title":
                        twitterTitle ??= content;

                        break;
                    case "og:description":
                        ogDescription ??= content;

                        break;
                    case "twitter:description":
                        twitterDescription ??= content;

                        break;
                    case "description":
                        metaDescription ??= content;

                        break;
                    case "og:image":
                    case "og:image:url":
                    case "og:image:secure_url":
                        ogImage ??= content;

                        break;
                    case "twitter:image":
                    case "twitter:image:src":
                        twitterImage ??= content;

                        break;
                }
            }

            var title = TitleTag().Match(head);

            if (title.Success)
                documentTitle = title.Groups[1].Value;
        }
        catch (RegexMatchTimeoutException)
        {
            // A page built to stall the parser gets no preview rather than the worker's time
            return null;
        }

        var preview = new LinkPreview
        {
            Title = Clean(ogTitle, LinkPreview.MaxTitleLength) ??
                    Clean(twitterTitle, LinkPreview.MaxTitleLength) ??
                    Clean(documentTitle, LinkPreview.MaxTitleLength),
            Description = Clean(ogDescription, LinkPreview.MaxDescriptionLength) ??
                          Clean(twitterDescription, LinkPreview.MaxDescriptionLength) ??
                          Clean(metaDescription, LinkPreview.MaxDescriptionLength),
            ImageUrl = ImageUrl(ogImage, pageUrl) ?? ImageUrl(twitterImage, pageUrl),
            FetchedAt = fetchedAt,
        };

        return preview is { Title: null, Description: null, ImageUrl: null }
            ? null
            : preview;
    }

    /// <summary>
    ///     The head of the page: up to <c>&lt;/head&gt;</c>, or to <c>&lt;body&gt;</c> on a page without one,
    ///     or all of it when neither is there.
    /// </summary>
    private static string HeadOf(string html)
    {
        var end = html.IndexOf("</head", StringComparison.OrdinalIgnoreCase);

        if (end < 0)
            end = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);

        return end < 0
            ? html
            : html[..end];
    }

    /// <summary>
    ///     A tag's attributes by lower-case name, with their quotes and entities taken off.
    /// </summary>
    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match attribute in Attribute().Matches(tag))
        {
            var name = attribute.Groups[1].Value.ToLowerInvariant();

            var value = attribute.Groups[2].Success
                ? attribute.Groups[2].Value
                : attribute.Groups[3].Success
                    ? attribute.Groups[3].Value
                    : attribute.Groups[4].Value;

            attributes.TryAdd(name, WebUtility.HtmlDecode(value));
        }

        return attributes;
    }

    /// <summary>
    ///     The text with its entities decoded, control characters dropped and whitespace collapsed, cut to
    ///     <paramref name="maxLength" /> characters; <c>null</c> when nothing is left.
    /// </summary>
    internal static string? Clean(string? text, int maxLength)
    {
        if (text is null)
            return null;

        // Tabs and newlines are control characters too, but they are whitespace to collapse, not noise to drop
        var cleaned = Whitespace()
            .Replace(string.Concat(WebUtility.HtmlDecode(text).Where(c => !char.IsControl(c) || char.IsWhiteSpace(c))), " ")
            .Trim();

        if (cleaned.Length == 0)
            return null;

        if (cleaned.Length <= maxLength)
            return cleaned;

        // Not cut through a surrogate pair: a lone surrogate is not valid in the JSON the clients read
        var end = char.IsHighSurrogate(cleaned[maxLength - 1])
            ? maxLength - 1
            : maxLength;

        return cleaned[..end].TrimEnd();
    }

    /// <summary>
    ///     The image as an absolute http or https URL, resolved against the page for a relative one; <c>null</c>
    ///     for anything else, such as a data URL or one too long to store.
    /// </summary>
    internal static string? ImageUrl(string? value, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(pageUrl, value.Trim(), out var url))
            return null;

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            return null;

        return url.AbsoluteUri.Length <= LinkPreview.MaxImageUrlLength
            ? url.AbsoluteUri
            : null;
    }

    /// <summary>
    ///     Comments, scripts and styles: a meta tag quoted inside one is not a meta tag.
    /// </summary>
    [GeneratedRegex(@"<!--.*?-->|<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        1000)]
    private static partial Regex Clutter();

    /// <summary>
    ///     A meta tag and its attributes. A quoted attribute value may contain a closing angle bracket.
    /// </summary>
    [GeneratedRegex("""<meta\b((?:[^>"']|"[^"]*"|'[^']*')*)>""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex TitleTag();

    /// <summary>
    ///     One attribute: its name, and its value in double quotes, single quotes or bare.
    /// </summary>
    [GeneratedRegex("""([^\s="'/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?""", RegexOptions.None, 1000)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Whitespace();
}
