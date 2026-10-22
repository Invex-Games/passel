using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     How many event streams may be open at once, bound from the <c>EventStreams</c> configuration section.
/// </summary>
internal sealed class EventStreamOptions
{
    public const string SectionName = "EventStreams";

    /// <summary>
    ///     Streams one user may have open on one instance. Opening another closes the oldest, so a client that
    ///     reconnects more often than it closes its old stream never loses events for long.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxStreamsPerUser { get; set; } = 5;

    /// <summary>
    ///     Streams one instance keeps open across all users. Beyond it, the endpoint answers 503. Keep it below the
    ///     Kestrel connection limit so other requests still get through.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxStreamsPerInstance { get; set; } = 4000;
}
