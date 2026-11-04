// ReSharper disable CheckNamespace

using JetBrains.Annotations;

namespace Microsoft.Extensions.Hosting;

/// <summary>
///     Keeps an outgoing request out of the traces. For a request whose URL is user content, such as the fetch of a
///     page a user linked: its client span would otherwise carry the URL's host and path (only the query is redacted)
///     to the trace exporter, Application Insights in Azure. <see cref="Extensions" /> applies it to every HttpClient
///     instrumentation registered, the Azure Monitor distro's included.
/// </summary>
[PublicAPI]
public static class HttpRequestTelemetry
{
    private static readonly HttpRequestOptionsKey<bool> ExcludedKey = new("Passel.Telemetry.Excluded");

    public static void ExcludeFromTelemetry(this HttpRequestMessage request) =>
        request.Options.Set(ExcludedKey, true);

    public static bool IsExcludedFromTelemetry(this HttpRequestMessage request) =>
        request.Options.TryGetValue(ExcludedKey, out var excluded) && excluded;
}
