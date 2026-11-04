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
    ///     Packet list and delete requests, and device lists, a user may make per minute.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int ReadsPerMinute { get; set; } = 120;

    /// <summary>
    ///     Device registrations, renames, removals and push token changes a user may make per minute. Clients make
    ///     one or two of these on every start and the rest from settings, so the limit is low.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int DeviceWritesPerMinute { get; set; } = 20;

    /// <summary>
    ///     Event streams a user may open per minute. Open streams are capped separately by the <c>EventStreams</c>
    ///     configuration.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int StreamsPerMinute { get; set; } = 10;
}
