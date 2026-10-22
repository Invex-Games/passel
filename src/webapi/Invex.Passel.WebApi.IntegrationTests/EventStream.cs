using System.Globalization;
using System.Net;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     One event from a <c>text/event-stream</c> response: its <c>event</c> type, its <c>data</c> lines joined with
///     newlines, and its <c>retry</c> value in milliseconds when it carries one.
/// </summary>
internal sealed record ServerSentEvent(string EventType, string Data, int? Retry);

/// <summary>
///     A client on <c>GET /api/packets/events</c>: reads the response one event at a time, parsed the way a
///     browser's <c>EventSource</c> parses it.
/// </summary>
internal sealed class EventStream : IAsyncDisposable
{
    public const string ResyncEventType = "resync";
    public const string PacketCreatedEventType = "packet-created";
    public const string PacketDeletedEventType = "packet-deleted";
    public const string HeartbeatEventType = "ping";

    /// <summary>
    ///     How long a test waits for an event it expects; a lost event fails the test rather than hanging it.
    /// </summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private EventStream(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    /// <summary>
    ///     Opens the caller's stream and checks that the response is an event stream.
    /// </summary>
    public static async Task<EventStream> OpenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(PacketApi.EventsPath,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return new(response, new StreamReader(stream));
    }

    /// <summary>
    ///     The next event other than a heartbeat; fails when none arrives within <see cref="EventTimeout" />.
    /// </summary>
    public async Task<ServerSentEvent> ReadAsync(CancellationToken cancellationToken) =>
        await TryReadAsync(EventTimeout, cancellationToken) ??
        throw new TimeoutException($"No event arrived on the stream within {EventTimeout}.");

    /// <summary>
    ///     The next event other than a heartbeat, or <c>null</c> when none arrives within <paramref name="timeout" />.
    /// </summary>
    public async Task<ServerSentEvent?> TryReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var sse = await ReadEventAsync(timeoutSource.Token);

                // Heartbeats keep the connection alive; they say nothing about packets
                if (sse.EventType != HeartbeatEventType)
                    return sse;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<ServerSentEvent> ReadEventAsync(CancellationToken cancellationToken)
    {
        string? eventType = null;
        int? retry = null;
        var data = new List<string>();

        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken) ??
                       throw new EndOfStreamException("The event stream ended.");

            if (line.Length == 0)
            {
                // A blank line dispatches the event; one with nothing buffered dispatches nothing
                if (eventType is null && retry is null && data.Count == 0)
                    continue;

                return new(eventType ?? "message", string.Join('\n', data), retry);
            }

            // A comment, such as a keep-alive
            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');

            var field = colon < 0
                ? line
                : line[..colon];

            var value = colon < 0
                ? string.Empty
                : line[(colon + 1)..];

            // One space after the colon is part of the syntax, not of the value
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    eventType = value;

                    break;
                case "data":
                    data.Add(value);

                    break;
                case "retry":
                    retry = int.Parse(value, CultureInfo.InvariantCulture);

                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // Disposing the response aborts the request, which ends the stream on the server
        _reader.Dispose();
        _response.Dispose();

        return ValueTask.CompletedTask;
    }
}
