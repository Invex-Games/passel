using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How many requests a user may make per minute, bound from the <c>RateLimits</c> configuration section. Each
///     limit is a sliding window; requests over it are rejected with 429.
/// </summary>
internal sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>
    ///     Packets a user may create per minute.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int CreatesPerMinute { get; set; } = 30;

    /// <summary>
    ///     List and delete requests a user may make per minute.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int ReadsPerMinute { get; set; } = 120;

    /// <summary>
    ///     Event streams a user may open per minute. Open streams are capped separately by the <c>EventStreams</c>
    ///     configuration.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int StreamsPerMinute { get; set; } = 10;
}
