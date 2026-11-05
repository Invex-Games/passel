using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Hosting;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The fetcher against a fake web: a handler that answers from a script and a resolver that stands in for
///     DNS, so the URL guards, the redirect handling and the size and time limits are checked without a network.
///     The address table itself is covered by <see cref="LinkPreviewAddressPolicyTests" />.
/// </summary>
public sealed class LinkPreviewFetcherTests
{
    private const string Html = "<html><head><title>Example Domain</title></head><body>Hello</body></html>";

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    /// <summary>
    ///     Answers each request from <paramref name="respond" /> and records what was asked for.
    /// </summary>
    // Answers each request after a delay that the request's token can cut short
    private sealed class DelayingHandler(TimeSpan delay, Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            return respond(request);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    ///     A request that never gets an answer, as to a host that accepts the connection and then says nothing.
    /// </summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new UnreachableException();
        }
    }

    /// <summary>
    ///     A body that arrives in chunks of a fixed size and counts what was read of it.
    /// </summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(chunkSize, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            BytesRead += length;

            return length;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    ///     A body whose first byte never comes.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    ///     The DNS of the fake web: a few names with known answers, so no test touches the network.
    /// </summary>
    private static Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IPAddress[]>(host switch
        {
            "example.com" => [IPAddress.Parse("93.184.216.34")],
            "v6.example.com" => [IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), IPAddress.Parse("93.184.216.34")],
            "localhost" => [IPAddress.Loopback],
            "intranet.corp" => [IPAddress.Parse("10.1.2.3")],
            "metadata.google.internal" => [IPAddress.Parse("169.254.169.254")],
            "mixed.example.com" => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")],
            "nowhere.example.com" => [],
            _ => throw new SocketException((int)SocketError.HostNotFound),
        });

    private static LinkPreviewFetcher CreateFetcher(
        HttpMessageHandler handler,
        Action<LinkPreviewOptions>? configure = null)
    {
        var options = new LinkPreviewOptions();
        configure?.Invoke(options);

        return new(Microsoft.Extensions.Options.Options.Create(options),
            new LinkPreviewAddressPolicy(),
            handler,
            Resolve);
    }

    private static HttpResponseMessage Page(
        string html,
        string contentType = "text/html; charset=utf-8",
        HttpStatusCode status = HttpStatusCode.OK) =>
        Bytes(Encoding.UTF8.GetBytes(html), contentType, status);

    private static HttpResponseMessage Bytes(
        byte[] body,
        string contentType = "text/html",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };

        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string? location)
    {
        var response = new HttpResponseMessage(status);

        if (location is not null)
            response.Headers.Location = new(location, UriKind.RelativeOrAbsolute);

        return response;
    }

    private static async Task<LinkPreviewFetchResult.Failed> FetchFailureAsync(LinkPreviewFetcher fetcher, string url)
    {
        var result = await fetcher.FetchAsync(new(url), CancellationToken);

        return Assert.IsType<LinkPreviewFetchResult.Failed>(result);
    }

    private static async Task<LinkPreviewFetchResult.Page> FetchPageAsync(LinkPreviewFetcher fetcher, string url)
    {
        var result = await fetcher.FetchAsync(new(url), CancellationToken);

        return Assert.IsType<LinkPreviewFetchResult.Page>(result);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:secret@example.com/")]
    [InlineData("https://user@example.com/")]
    public async Task Refuses_urls_that_are_not_plain_http_without_asking_the_web(string url)
    {
        var handler = new FakeHandler(_ => Page(Html));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, url);

        Assert.False(failed.Transient);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:5218/api/packets")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1:8080/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://localhost/")]
    [InlineData("https://intranet.corp/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://mixed.example.com/")]
    public async Task Refuses_hosts_that_resolve_to_an_address_the_policy_refuses(string url)
    {
        var handler = new FakeHandler(_ => Page(Html));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, url);

        Assert.False(failed.Transient);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://nowhere.example.com/")]
    [InlineData("http://unknown.example.com/")]
    public async Task A_host_that_does_not_resolve_is_a_transient_failure(string url)
    {
        var handler = new FakeHandler(_ => Page(Html));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, url);

        Assert.True(failed.Transient);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetches_a_public_page_as_passel()
    {
        var handler = new FakeHandler(_ => Page(Html));
        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://v6.example.com/path?q=1");

        Assert.Equal(new Uri("https://v6.example.com/path?q=1"), page.Url);
        Assert.Equal(Html, page.Html);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(LinkPreviewFetcher.UserAgent, request.Headers.UserAgent.ToString());
        Assert.Contains("text/html", request.Headers.Accept.ToString());

        // The linked URL is user content, so its request records no client span
        Assert.True(request.IsExcludedFromTelemetry());
    }

    [Fact]
    public async Task Follows_a_relative_redirect_and_reports_the_final_url()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/old"
            ? Redirect(HttpStatusCode.Found, "/new?from=old")
            : Page(Html));

        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://example.com/old");

        Assert.Equal(new Uri("https://example.com/new?from=old"), page.Url);
        Assert.Equal(["https://example.com/old", "https://example.com/new?from=old"],
            handler.Requests.Select(request => request.RequestUri!.ToString()));
    }

    [Theory]
    [InlineData("http://intranet.corp/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("ftp://example.com/")]
    [InlineData("http://user:secret@example.com/")]
    public async Task A_redirect_is_checked_like_the_first_url(string location)
    {
        var handler = new FakeHandler(_ => Redirect(HttpStatusCode.MovedPermanently, location));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/start");

        Assert.False(failed.Transient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Gives_up_after_the_redirect_cap()
    {
        var hops = 0;
        var handler = new FakeHandler(_ => Redirect(HttpStatusCode.TemporaryRedirect, $"/hop{++hops}"));
        using var fetcher = CreateFetcher(handler, options => options.MaxRedirects = 2);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/start");

        Assert.False(failed.Transient);
        Assert.Contains("redirects", failed.Reason);

        // The start, and the two redirects allowed; the third is not followed
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_without_a_location_is_a_final_failure()
    {
        var handler = new FakeHandler(_ => Redirect(HttpStatusCode.Found, null));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.False(failed.Transient);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("image/png")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/octet-stream")]
    public async Task Only_html_is_read(string contentType)
    {
        var handler = new FakeHandler(_ => Page(Html, contentType));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.False(failed.Transient);
        Assert.Contains("not HTML", failed.Reason);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML; charset=UTF-8")]
    [InlineData("application/xhtml+xml")]
    public async Task Html_of_either_media_type_is_read(string contentType)
    {
        var handler = new FakeHandler(_ => Page(Html, contentType));
        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://example.com/");

        Assert.Equal(Html, page.Html);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Gone, false)]
    public async Task Server_trouble_is_transient_and_a_client_error_is_final(HttpStatusCode status, bool transient)
    {
        var handler = new FakeHandler(_ => Page(Html, status: status));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.Equal(transient, failed.Transient);
        Assert.Contains(((int)status).ToString(), failed.Reason);
    }

    [Fact]
    public async Task A_connection_failure_is_transient()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        using var fetcher = CreateFetcher(handler);

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.True(failed.Transient);
        Assert.Equal("Connection refused", failed.Reason);
    }

    [Fact]
    public async Task Reads_no_more_of_the_body_than_the_cap()
    {
        const int cap = 4096;
        var body = Encoding.UTF8.GetBytes("<html><head><title>Big page</title>" + new string('x', 100 * 1024));
        var stream = new ChunkedStream(body, 1024);

        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };

            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/html");

            return response;
        });

        using var fetcher = CreateFetcher(handler, options => options.MaxBodyBytes = cap);

        var page = await FetchPageAsync(fetcher, "https://example.com/big");

        Assert.Equal(cap, stream.BytesRead);
        Assert.Equal(cap, page.Html.Length);
        Assert.StartsWith("<html><head><title>Big page</title>", page.Html);
    }

    [Fact]
    public async Task Stops_reading_at_the_end_of_the_head()
    {
        var body = Encoding.UTF8.GetBytes("<html><head><title>Short head</title></HEAD><body>" + new string('x', 1024 * 1024));
        var stream = new ChunkedStream(body, 1024);

        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };

            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/html");

            return response;
        });

        using var fetcher = CreateFetcher(handler, options => options.MaxBodyBytes = 4 * 1024 * 1024);

        var page = await FetchPageAsync(fetcher, "https://example.com/long");

        // The head ends in the first chunk, so that is all that is read
        Assert.Equal(1024, stream.BytesRead);
        Assert.Contains("<title>Short head</title>", page.Html);
    }

    [Fact]
    public async Task Gives_up_when_the_page_does_not_answer_in_time()
    {
        using var fetcher = CreateFetcher(new SilentHandler(), options => options.Timeout = TimeSpan.FromMilliseconds(200));

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.True(failed.Transient);
        Assert.Contains("longer than", failed.Reason);
    }

    [Fact]
    public async Task Gives_up_when_the_body_does_not_arrive_in_time()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream()),
            };

            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/html");

            return response;
        });

        using var fetcher = CreateFetcher(handler, options => options.Timeout = TimeSpan.FromMilliseconds(200));

        var failed = await FetchFailureAsync(fetcher, "https://example.com/");

        Assert.True(failed.Transient);
        Assert.Contains("longer than", failed.Reason);
    }

    [Fact]
    public async Task The_time_budget_covers_the_whole_fetch_redirects_included()
    {
        // Each hop takes a good share of the budget; together they are over it. The hops wait on the token rather
        // than block a thread, so the budget cuts the second one off however late its timer runs
        var handler = new DelayingHandler(TimeSpan.FromMilliseconds(120),
            request => request.RequestUri!.AbsolutePath == "/final"
                ? Page(Html)
                : Redirect(HttpStatusCode.Found, "/final"));

        using var fetcher = CreateFetcher(handler, options => options.Timeout = TimeSpan.FromMilliseconds(200));

        var failed = await FetchFailureAsync(fetcher, "https://example.com/start");

        Assert.True(failed.Transient);
    }

    [Fact]
    public async Task A_cancelled_fetch_is_not_a_failure_but_a_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        using var fetcher = CreateFetcher(new SilentHandler());

        var fetching = fetcher.FetchAsync(new("https://example.com/"), cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
    }

    [Fact]
    public async Task Decodes_the_charset_the_response_names()
    {
        var body = Encoding.Latin1.GetBytes("<html><head><title>Café</title></head>");
        var handler = new FakeHandler(_ => Bytes(body, "text/html; charset=iso-8859-1"));
        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://example.com/");

        Assert.Contains("<title>Café</title>", page.Html);
    }

    [Fact]
    public async Task Decodes_the_charset_the_page_names_when_the_response_names_none()
    {
        // Smart quotes are 0x93 and 0x94 in windows-1252, which the runtime only knows once the code pages are loaded
        var body = Encoding.Latin1
            .GetBytes("<html><head><meta charset=\"windows-1252\"><title>\u0093Quoted\u0094</title></head>")
            .ToArray();

        var handler = new FakeHandler(_ => Bytes(body, "text/html"));
        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://example.com/");

        Assert.Contains("<title>“Quoted”</title>", page.Html);
    }

    [Fact]
    public async Task Falls_back_to_utf8()
    {
        var body = Encoding.UTF8.GetBytes("<html><head><title>Café</title></head>");
        var handler = new FakeHandler(_ => Bytes(body, "text/html"));
        using var fetcher = CreateFetcher(handler);

        var page = await FetchPageAsync(fetcher, "https://example.com/");

        Assert.Contains("<title>Café</title>", page.Html);
    }

    [Fact]
    public void An_unknown_charset_falls_back_to_utf8()
    {
        var body = Encoding.UTF8.GetBytes("Café");

        Assert.Equal("Café", LinkPreviewFetcher.Decode(body, "no-such-charset"));
    }
}
