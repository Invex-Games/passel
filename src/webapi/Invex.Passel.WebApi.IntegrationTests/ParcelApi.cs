using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     An uploaded file as the API returns it, read straight from the JSON.
/// </summary>
internal sealed record ParcelJson(
    string ParcelId,
    string? FileName,
    string ContentType,
    long Size,
    bool IsImage,
    int? Width,
    int? Height)
{
    /// <summary>
    ///     Every property an upload answers with; nothing else may appear.
    /// </summary>
    public static readonly string[] PropertyNames =
        ["contentType", "fileName", "height", "isImage", "parcelId", "size", "width"];

    public static ParcelJson Parse(JsonElement parcel)
    {
        Assert.Equal(PropertyNames, ContractTests.PropertyNames(parcel));

        return new(parcel.GetProperty("parcelId").GetString()!,
            PacketJson.OptionalString(parcel, "fileName"),
            parcel.GetProperty("contentType").GetString()!,
            parcel.GetProperty("size").GetInt64(),
            parcel.GetProperty("isImage").GetBoolean(),
            OptionalInt32(parcel, "width"),
            OptionalInt32(parcel, "height"));
    }

    private static int? OptionalInt32(JsonElement element, string name) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.Number } value
            ? value.GetInt32()
            : null;
}

/// <summary>
///     The file endpoints, called the way a client calls them: an upload is a <c>multipart/form-data</c> body with
///     one part named <c>file</c>, as a browser's <c>FormData</c> sends it. Calls that return a file check the status
///     they expect and fail with the response body when they get another; the rest return the response for the test
///     to judge.
/// </summary>
internal static class ParcelApi
{
    public const string ParcelsPath = "/api/parcels";

    /// <summary>
    ///     Uploads <paramref name="bytes" /> as the <c>file</c> part, declared as <paramref name="partContentType" />
    ///     (which the API ignores). Without a length, the body is sent chunked.
    /// </summary>
    public static async Task<HttpResponseMessage> PostParcelAsync(
        this HttpClient client,
        byte[] bytes,
        string? fileName,
        string partContentType,
        CancellationToken cancellationToken,
        bool withLength = true)
    {
        HttpContent part = withLength
            ? new ByteArrayContent(bytes)
            : new StreamContent(new UnknownLengthStream(bytes));
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(partContentType);

        // A part without a length leaves the whole form without one
        using var form = new MultipartFormDataContent();

        if (fileName is null)
            form.Add(part, "file");
        else
            form.Add(part, "file", fileName);

        return await client.PostAsync(ParcelsPath, form, cancellationToken);
    }

    public static async Task<ParcelJson> UploadAsync(
        this HttpClient client,
        byte[] bytes,
        string? fileName,
        string partContentType,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostParcelAsync(bytes, fileName, partContentType, cancellationToken);
        using var document = await PacketApi.ReadJsonAsync(response, HttpStatusCode.Created, cancellationToken);

        var parcel = ParcelJson.Parse(document.RootElement);

        Assert.Equal($"{ParcelsPath}/{parcel.ParcelId}", response.Headers.Location?.OriginalString);

        return parcel;
    }

    public static async Task<HttpResponseMessage> DownloadParcelAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ParcelsPath}/{id}");
        configure?.Invoke(request);

        return await client.SendAsync(request, cancellationToken);
    }

    public static Task<HttpResponseMessage> GetThumbnailAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken) =>
        client.GetAsync($"{ParcelsPath}/{id}/thumbnail", cancellationToken);

    public static async Task<HttpStatusCode> DiscardParcelAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync($"{ParcelsPath}/{id}", cancellationToken);

        return response.StatusCode;
    }

    /// <summary>
    ///     The bytes as a stream that cannot seek, so <see cref="StreamContent" /> cannot give them a length.
    /// </summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
