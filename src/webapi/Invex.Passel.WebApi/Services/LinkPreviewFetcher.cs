using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     What a fetch came back with.
/// </summary>
[Union]
internal partial record LinkPreviewFetchResult
{
    /// <summary>
    ///     The page: its URL after any redirects, and as much of its HTML as the fetcher read (the head, up to the
    ///     size cap).
    /// </summary>
    partial record Page(Uri Url, string Html);

    /// <summary>
    ///     No page. A failure that may pass, such as a slow or unreachable site, is <paramref name="Transient" />
    ///     and worth another attempt; a refusal (an address the fetcher may not reach, one redirect too many, a
    ///     page that is not HTML, a 4xx answer) is final.
    /// </summary>
    partial record Failed(string Reason, bool Transient);
}

internal interface ILinkPreviewFetcher
{
    Task<LinkPreviewFetchResult> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
///     Fetches the HTML of a page the user linked, and nothing else. Every URL it requests, the one sent and each
///     redirect, must be http or https and carry no credentials; its host is resolved here, every address checked
///     with the <see cref="ILinkPreviewAddressPolicy" />, and the connection made to one of those checked addresses
///     rather than to whatever the name resolves to by then. Only <c>text/html</c> is read, at most
///     <see cref="LinkPreviewOptions.MaxBodyBytes" /> of it and no further than the end of the head, within one
///     time budget for the whole fetch.
/// </summary>
internal sealed partial class LinkPreviewFetcher : ILinkPreviewFetcher, IDisposable
{
    /// <summary>
    ///     Resolves a host name to its addresses. The default asks DNS; tests pass their own.
    /// </summary>
    internal delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    internal const string UserAgent = "Passel/1.0 (+https://passel.link)";

    private const string Accept = "text/html, application/xhtml+xml;q=0.9, */*;q=0.1";

    private static readonly string[] HtmlMediaTypes = ["text/html", "application/xhtml+xml"];

    /// <summary>
    ///     The addresses checked for a request, handed to the connection callback through the request's options.
    /// </summary>
    private static readonly HttpRequestOptionsKey<IPAddress[]> CheckedAddresses = new("Passel.LinkPreview.Addresses");

    private static readonly byte[] HeadEnd = "</head"u8.ToArray();
    private static readonly byte[] BodyStart = "<body"u8.ToArray();

    /// <summary>
    ///     How much of the body is searched for a <c>meta charset</c> when the response names no charset.
    /// </summary>
    private const int CharsetSniffBytes = 2048;

    static LinkPreviewFetcher() =>
        // Pages in the legacy single-byte encodings, such as windows-1252, need the code pages the runtime does
        // not load by default
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly LinkPreviewOptions _options;
    private readonly ILinkPreviewAddressPolicy _policy;
    private readonly HostResolver _resolve;
    private readonly HttpMessageInvoker _http;

    /// <summary>
    ///     Built by the container with the real handler and DNS. Tests pass a <paramref name="handler" /> that
    ///     answers for the web and a <paramref name="resolver" /> that stands in for DNS; the container passes
    ///     neither, as nothing registers those types.
    /// </summary>
    public LinkPreviewFetcher(
        IOptions<LinkPreviewOptions> options,
        ILinkPreviewAddressPolicy policy,
        HttpMessageHandler? handler = null,
        HostResolver? resolver = null)
    {
        _options = options.Value;
        _policy = policy;
        _resolve = resolver ?? ResolveWithDnsAsync;
        _http = new(handler ?? CreateHandler(_options.Timeout), disposeHandler: true);
    }

    private static SocketsHttpHandler CreateHandler(TimeSpan timeout) =>
        new()
        {
            // Redirects are followed by hand, so each hop is checked like the first URL
            AllowAutoRedirect = false,
            UseCookies = false,

            // A proxy would connect on the fetcher's behalf, to an address this handler never checked
            UseProxy = false,

            // No Accept-Encoding goes out, so the size cap counts the bytes as the page sends them
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectAsync,
        };

    public async Task<LinkPreviewFetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        // One budget for the whole fetch, redirects and the body included
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.Timeout);

        try
        {
            return await FetchWithinBudgetAsync(url, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LinkPreviewFetchResult.Failed(
                $"the fetch took longer than {_options.Timeout.TotalSeconds:0.#} seconds",
                Transient: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException)
        {
            return new LinkPreviewFetchResult.Failed(exception.Message, Transient: true);
        }
    }

    private async Task<LinkPreviewFetchResult> FetchWithinBudgetAsync(Uri url, CancellationToken cancellationToken)
    {
        var current = url;

        for (var redirects = 0;; redirects++)
        {
            if (Refuses(current) is { } refusal)
                return new LinkPreviewFetchResult.Failed(refusal, Transient: false);

            var addresses = await ResolveAsync(current, cancellationToken);

            if (addresses.Length == 0)
                return new LinkPreviewFetchResult.Failed($"{current.Host} did not resolve", Transient: true);

            if (!addresses.All(_policy.Allows))
                return new LinkPreviewFetchResult.Failed($"{current.Host} resolves to an address this server may not fetch from",
                    Transient: false);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", Accept);
            request.Options.Set(CheckedAddresses, addresses);

            // The invoker returns once the headers are in; the body is read below, up to the cap
            using var response = await _http.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;

            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= _options.MaxRedirects)
                    return new LinkPreviewFetchResult.Failed($"more than {_options.MaxRedirects} redirects",
                        Transient: false);

                if (response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var next))
                    return new LinkPreviewFetchResult.Failed("a redirect without a usable location", Transient: false);

                current = next;

                continue;
            }

            if (status is 429 or >= 500)
                return new LinkPreviewFetchResult.Failed($"the page answered {status}", Transient: true);

            if (!response.IsSuccessStatusCode)
                return new LinkPreviewFetchResult.Failed($"the page answered {status}", Transient: false);

            var contentType = response.Content.Headers.ContentType;

            if (!HtmlMediaTypes.Contains(contentType?.MediaType, StringComparer.OrdinalIgnoreCase))
                return new LinkPreviewFetchResult.Failed(
                    $"the page is {contentType?.MediaType ?? "of no stated type"}, not HTML",
                    Transient: false);

            var html = await ReadHeadAsync(response.Content, contentType?.CharSet, cancellationToken);

            return new LinkPreviewFetchResult.Page(current, html);
        }
    }

    /// <summary>
    ///     Why a URL may not be requested at all, or <c>null</c> when it may: only absolute http and https URLs
    ///     without credentials. The URL the user sent passed the same scheme check when the link was created; a
    ///     redirect can point anywhere.
    /// </summary>
    internal static string? Refuses(Uri url)
    {
        if (!url.IsAbsoluteUri)
            return "the URL is not absolute";

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            return $"the URL is {url.Scheme}, not http or https";

        if (url.UserInfo.Length > 0)
            return "the URL carries credentials";

        if (url.IdnHost.Length == 0)
            return "the URL has no host";

        return null;
    }

    /// <summary>
    ///     The addresses of the URL's host, IPv4 first: a literal address as it is, a name as the resolver says.
    /// </summary>
    private async Task<IPAddress[]> ResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        var host = url.IdnHost;

        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        var addresses = await _resolve(host, cancellationToken);

        return
        [
            ..addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork),
            ..addresses.Where(address => address.AddressFamily != AddressFamily.InterNetwork),
        ];
    }

    private static Task<IPAddress[]> ResolveWithDnsAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    ///     Connects to one of the addresses the fetcher checked for this request, never to whatever the host name
    ///     resolves to now: a name whose answer changes between the check and the connection (DNS rebinding) gets
    ///     the checked address all the same. TLS still validates the certificate against the host name.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(CheckedAddresses, out var addresses) ||
            addresses.Length == 0)
            throw new InvalidOperationException("The request carries no checked address to connect to.");

        Exception? lastFailure = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                socket.Dispose();
                lastFailure = exception;
            }
        }

        throw new HttpRequestException($"No address of {context.DnsEndPoint.Host} accepted a connection.", lastFailure);
    }

    /// <summary>
    ///     Reads the body up to the size cap, and no further than the end of the head, which is all the extractor
    ///     looks at; then decodes it with the charset the response named, the one the page names, or UTF-8.
    /// </summary>
    private async Task<string> ReadHeadAsync(HttpContent content, string? charset, CancellationToken cancellationToken)
    {
        var maxBytes = _options.MaxBodyBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var read = 0;
            var searched = 0;

            while (read < maxBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, maxBytes - read), cancellationToken);

                if (count == 0)
                    break;

                read += count;

                // Searched from a few bytes back, in case the tag straddles two reads
                var from = Math.Max(0, searched - HeadEnd.Length);

                if (ContainsTag(buffer.AsSpan(from, read - from), HeadEnd) ||
                    ContainsTag(buffer.AsSpan(from, read - from), BodyStart))
                    break;

                searched = read;
            }

            return Decode(buffer.AsSpan(0, read), charset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Whether the bytes contain the (lower-case, ASCII) tag in any letter case.
    /// </summary>
    private static bool ContainsTag(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> tag)
    {
        for (var start = 0; start + tag.Length <= bytes.Length; start++)
        {
            var matches = true;

            for (var i = 0; i < tag.Length && matches; i++)
            {
                var b = bytes[start + i];

                if (b is >= (byte)'A' and <= (byte)'Z')
                    b += 'a' - 'A';

                matches = b == tag[i];
            }

            if (matches)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Decodes the bytes read with the charset the response named, else the one a <c>meta charset</c> near the
    ///     top of the page names, else UTF-8. A charset the runtime does not know falls back to UTF-8 too.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, string? charset)
    {
        var encoding = ToEncoding(charset);

        if (encoding is null)
        {
            // The charset declaration is ASCII, so it reads the same in Latin-1 whatever the page's encoding
            var top = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, CharsetSniffBytes)]);
            var declared = MetaCharset().Match(top);

            if (declared.Success)
                encoding = ToEncoding(declared.Groups[1].Value);
        }

        return (encoding ?? Encoding.UTF8).GetString(bytes);
    }

    private static Encoding? ToEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return null;

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("""<meta[^>]*charset\s*=\s*["']?\s*([A-Za-z0-9._:-]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MetaCharset();

    public void Dispose() =>
        _http.Dispose();
}
