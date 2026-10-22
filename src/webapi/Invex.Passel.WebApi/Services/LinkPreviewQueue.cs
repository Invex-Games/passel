using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     A link whose page is to be fetched for a preview: the packet it belongs to, its owner, and the URL as the
///     user sent it. Nothing but that URL (and where it redirects) is ever fetched.
/// </summary>
internal sealed record LinkPreviewJob(Guid PacketId, string UserId, string Url);

internal interface ILinkPreviewQueue
{
    /// <summary>
    ///     Queues a fetch for after the link has been committed. Returns <c>false</c> when previews are off or the
    ///     queue is full; the link then simply stays without a preview.
    /// </summary>
    bool TryEnqueue(LinkPreviewJob job);
}

/// <summary>
///     The fetches waiting for <see cref="LinkPreviewWorker" /> on this instance. In memory only: a fetch queued
///     here is lost if the instance stops before it ran, and the link keeps no preview, which is the same as a
///     page that gave none.
/// </summary>
internal sealed partial class LinkPreviewQueue(IOptions<LinkPreviewOptions> options, ILogger<LinkPreviewQueue> logger)
    : ILinkPreviewQueue
{
    private readonly bool _enabled = options.Value.Enabled;

    // Bounded, and a write to a full queue fails at once rather than waiting: a preview is a nicety, and a flood of
    // links must not hold up the requests that create them
    private readonly Channel<LinkPreviewJob> _channel = Channel.CreateBounded<LinkPreviewJob>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<LinkPreviewJob> Reader =>
        _channel.Reader;

    public bool TryEnqueue(LinkPreviewJob job)
    {
        if (!_enabled)
            return false;

        if (_channel.Writer.TryWrite(job))
            return true;

        LogQueueFull(job.PacketId);

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The link preview queue is full; link {PacketId} will have no preview")]
    private partial void LogQueueFull(Guid packetId);
}
