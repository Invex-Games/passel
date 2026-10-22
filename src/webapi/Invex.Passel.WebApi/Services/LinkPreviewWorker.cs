using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     Fetches the pages queued by <see cref="LinkPreviewQueue" />, a few at a time, and attaches what they say
///     about themselves to their links. A page that gives nothing, or cannot be fetched after the retries, leaves
///     its link as it was: a link without a preview is the normal case, not an error.
/// </summary>
internal sealed partial class LinkPreviewWorker(
    LinkPreviewQueue queue,
    ILinkPreviewFetcher fetcher,
    IServiceScopeFactory scopeFactory,
    IOptions<LinkPreviewOptions> options,
    ILogger<LinkPreviewWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled();

            return Task.CompletedTask;
        }

        return Task.WhenAll(Enumerable
            .Range(0, options.Value.MaxConcurrentFetches)
            .Select(_ => RunAsync(stoppingToken)));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // One link's surprise must not stop the others
                    LogFailed(exception, job.PacketId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(LinkPreviewJob job, CancellationToken cancellationToken)
    {
        // Validated when the link was created; the fetcher checks it again in any case
        if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var url))
            return;

        var fetched = await FetchWithRetriesAsync(fetcher,
            url,
            options.Value.MaxAttempts,
            options.Value.RetryDelay,
            cancellationToken);

        if (fetched is LinkPreviewFetchResult.Failed failed)
        {
            LogNoPreview(job.PacketId, failed.Reason);

            return;
        }

        var page = (LinkPreviewFetchResult.Page)fetched;
        var preview = LinkPreviewExtractor.Extract(page.Html, page.Url, DateTimeOffset.UtcNow);

        if (preview is null)
        {
            LogNoPreview(job.PacketId, "the page has no title, description or image");

            return;
        }

        // The store service is scoped, like the session it writes with
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPacketStoreService>();

        var stored = await store.Handle(new SetLinkPreview
        {
            UserId = job.UserId,
            PacketId = job.PacketId,
            Preview = preview,
        });

        if (stored)
            LogStored(job.PacketId);
        else
            LogGone(job.PacketId);
    }

    /// <summary>
    ///     Fetches the page, trying again after a transient failure up to <paramref name="maxAttempts" /> times in
    ///     all, with <paramref name="retryDelay" /> before the second attempt and a multiple of it before each later
    ///     one. A final failure is returned as it is; so is the last transient one once the attempts are used up.
    /// </summary>
    internal static async Task<LinkPreviewFetchResult> FetchWithRetriesAsync(
        ILinkPreviewFetcher fetcher,
        Uri url,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1;; attempt++)
        {
            var result = await fetcher.FetchAsync(url, cancellationToken);

            if (result is not LinkPreviewFetchResult.Failed { Transient: true } || attempt >= maxAttempts)
                return result;

            await Task.Delay(retryDelay * attempt, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Link previews are off; links are stored without one")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "No preview for link {PacketId}: {Reason}")]
    private partial void LogNoPreview(Guid packetId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stored the preview of link {PacketId}")]
    private partial void LogStored(Guid packetId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Link {PacketId} was deleted before its preview was fetched; nothing stored")]
    private partial void LogGone(Guid packetId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fetching the preview of link {PacketId} failed unexpectedly")]
    private partial void LogFailed(Exception exception, Guid packetId);
}
