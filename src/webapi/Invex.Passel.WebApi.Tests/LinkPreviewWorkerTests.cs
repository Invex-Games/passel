using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The retry policy of the preview worker: a transient failure is tried again a few times, a final one is not,
///     and the attempts run out.
/// </summary>
public sealed class LinkPreviewWorkerTests
{
    private static readonly Uri Url = new("https://example.com/");

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static readonly LinkPreviewFetchResult Page = new LinkPreviewFetchResult.Page(Url, "<html></html>");
    private static readonly LinkPreviewFetchResult Transient = new LinkPreviewFetchResult.Failed("down", Transient: true);
    private static readonly LinkPreviewFetchResult Final = new LinkPreviewFetchResult.Failed("not HTML", Transient: false);

    /// <summary>
    ///     Answers one scripted result per call, the last one again once the script runs out.
    /// </summary>
    private sealed class ScriptedFetcher(params LinkPreviewFetchResult[] script) : ILinkPreviewFetcher
    {
        public int Calls { get; private set; }

        public Task<LinkPreviewFetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            Assert.Equal(Url, url);

            var result = script[Math.Min(Calls, script.Length - 1)];
            Calls++;

            return Task.FromResult(result);
        }
    }

    private static Task<LinkPreviewFetchResult> FetchAsync(ScriptedFetcher fetcher, int maxAttempts = 3) =>
        LinkPreviewWorker.FetchWithRetriesAsync(fetcher, Url, maxAttempts, TimeSpan.Zero, CancellationToken);

    [Fact]
    public async Task A_page_is_returned_at_once()
    {
        var fetcher = new ScriptedFetcher(Page);

        Assert.Same(Page, await FetchAsync(fetcher));
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task A_transient_failure_is_tried_again_until_the_page_comes()
    {
        var fetcher = new ScriptedFetcher(Transient, Transient, Page);

        Assert.Same(Page, await FetchAsync(fetcher));
        Assert.Equal(3, fetcher.Calls);
    }

    [Fact]
    public async Task The_attempts_run_out_and_the_last_failure_is_returned()
    {
        var fetcher = new ScriptedFetcher(Transient);

        Assert.Same(Transient, await FetchAsync(fetcher));
        Assert.Equal(3, fetcher.Calls);
    }

    [Fact]
    public async Task A_final_failure_is_not_tried_again()
    {
        var fetcher = new ScriptedFetcher(Final, Page);

        Assert.Same(Final, await FetchAsync(fetcher));
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task A_failure_that_turns_final_stops_the_retries()
    {
        var fetcher = new ScriptedFetcher(Transient, Final, Page);

        Assert.Same(Final, await FetchAsync(fetcher));
        Assert.Equal(2, fetcher.Calls);
    }

    [Fact]
    public async Task One_attempt_means_no_retry()
    {
        var fetcher = new ScriptedFetcher(Transient, Page);

        Assert.Same(Transient, await FetchAsync(fetcher, maxAttempts: 1));
        Assert.Equal(1, fetcher.Calls);
    }
}
