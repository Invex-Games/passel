using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How link previews are fetched, bound from the <c>LinkPreviews</c> configuration section. The guards against
///     reaching private addresses are not configurable; see <see cref="LinkPreviewAddressPolicy" />.
/// </summary>
internal sealed class LinkPreviewOptions
{
    public const string SectionName = "LinkPreviews";

    /// <summary>
    ///     Whether pages are fetched at all. Off, links are stored without a preview.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How long one attempt may take in all: resolving, connecting, every redirect and reading the body. Over it,
    ///     the attempt fails and may be retried.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The most bytes read from a page. The tags a preview needs sit in the head, so the rest of a long page is
    ///     never read anyway.
    /// </summary>
    [Range(1024, 16 * 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 512 * 1024;

    /// <summary>
    ///     How many redirects one fetch follows. Each hop is checked like the URL the user sent.
    /// </summary>
    [Range(0, 10)]
    public int MaxRedirects { get; set; } = 3;

    /// <summary>
    ///     How often a fetch is attempted before the link is left without a preview. Only failures that may pass
    ///     (a timeout, a connection error, a 429 or 5xx answer) are retried.
    /// </summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    ///     The wait before the second attempt; each later attempt waits that much longer again.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How many pages one instance fetches at the same time.
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrentFetches { get; set; } = 4;

    /// <summary>
    ///     How many links may wait for a fetch on one instance. Beyond it, new links go without a preview.
    /// </summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = 1000;
}
