using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     A device as the API returns it, read straight from the JSON.
/// </summary>
internal sealed record DeviceJson(
    string Id,
    string Name,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? PushUpdatedAt)
{
    /// <summary>
    ///     Every property a device has on the wire; nothing else may appear, the push token least of all.
    /// </summary>
    public static readonly string[] PropertyNames = ["createdAt", "id", "lastSeenAt", "name", "platform", "pushUpdatedAt"];

    public static DeviceJson Parse(JsonElement device)
    {
        Assert.Equal(PropertyNames, ContractTests.PropertyNames(device));

        var pushUpdatedAt = device.GetProperty("pushUpdatedAt");

        return new(device.GetProperty("id").GetString()!,
            device.GetProperty("name").GetString()!,
            device.GetProperty("platform").GetString()!,
            device.GetProperty("createdAt").GetDateTimeOffset(),
            device.GetProperty("lastSeenAt").GetDateTimeOffset(),
            pushUpdatedAt.ValueKind == JsonValueKind.Null
                ? null
                : pushUpdatedAt.GetDateTimeOffset());
    }
}

/// <summary>
///     The device endpoints, called the way a client calls them. Calls that return a device check the status they
///     expect and fail with the response body when they get another; the rest return the response for the test to
///     judge.
/// </summary>
internal static class DeviceApi
{
    public const string DevicesPath = "/api/devices";

    /// <summary>
    ///     An installation id as a client would generate one.
    /// </summary>
    public static string NewInstallationId() =>
        Guid.NewGuid().ToString();

    public static JsonObject Registration(string installationId, string name, string platform) =>
        new()
        {
            ["installationId"] = installationId,
            ["name"] = name,
            ["platform"] = platform,
        };

    public static JsonObject FcmToken(string token) =>
        new()
        {
            ["$type"] = "Fcm",
            ["token"] = token,
        };

    /// <summary>
    ///     A Web Push subscription as <c>PushSubscription.toJSON()</c> gives it, discriminator added: an endpoint of
    ///     its own and well-formed keys.
    /// </summary>
    public static JsonObject WebPushSubscription(string endpoint) =>
        new()
        {
            ["$type"] = "WebPush",
            ["endpoint"] = endpoint,
            ["expirationTime"] = null,
            ["keys"] = new JsonObject
            {
                ["p256dh"] = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM",
                ["auth"] = "tBHItJI5svbpez7KI4CCXg",
            },
        };

    public static Task<HttpResponseMessage> PostDeviceAsync(
        this HttpClient client,
        JsonObject body,
        CancellationToken cancellationToken) =>
        client.PostAsync(DevicesPath, Json(body), cancellationToken);

    /// <summary>
    ///     Registers the installation and expects <paramref name="expected" />: 201 for a new device, 200 for one
    ///     registered already.
    /// </summary>
    public static async Task<DeviceJson> RegisterAsync(
        this HttpClient client,
        string installationId,
        string name,
        string platform,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostDeviceAsync(Registration(installationId, name, platform), cancellationToken);
        using var document = await PacketApi.ReadJsonAsync(response, expected, cancellationToken);

        var device = DeviceJson.Parse(document.RootElement);

        if (expected == HttpStatusCode.Created)
            Assert.Equal($"{DevicesPath}/{device.Id}", response.Headers.Location?.OriginalString);

        return device;
    }

    public static async Task<IReadOnlyList<DeviceJson>> ListDevicesAsync(
        this HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(DevicesPath, cancellationToken);
        using var document = await PacketApi.ReadJsonAsync(response, HttpStatusCode.OK, cancellationToken);

        Assert.Equal(["devices"], ContractTests.PropertyNames(document.RootElement));

        return document
            .RootElement
            .GetProperty("devices")
            .EnumerateArray()
            .Select(DeviceJson.Parse)
            .ToList();
    }

    public static Task<HttpResponseMessage> RenameDeviceAsync(
        this HttpClient client,
        string id,
        string? name,
        CancellationToken cancellationToken) =>
        client.PatchAsync($"{DevicesPath}/{id}",
            Json(new JsonObject
            {
                ["name"] = name,
            }),
            cancellationToken);

    public static async Task<HttpStatusCode> RemoveDeviceAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync($"{DevicesPath}/{id}", cancellationToken);

        return response.StatusCode;
    }

    public static Task<HttpResponseMessage> PutPushAsync(
        this HttpClient client,
        string id,
        JsonObject token,
        CancellationToken cancellationToken) =>
        client.PutAsync($"{DevicesPath}/{id}/push", Json(token), cancellationToken);

    public static async Task<HttpStatusCode> ClearPushAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync($"{DevicesPath}/{id}/push", cancellationToken);

        return response.StatusCode;
    }

    /// <summary>
    ///     The caller's device with this id, as the list shows it.
    /// </summary>
    public static async Task<DeviceJson> ListedAsync(
        this HttpClient client,
        string id,
        CancellationToken cancellationToken) =>
        Assert.Single(await client.ListDevicesAsync(cancellationToken), device => device.Id == id);

    private static StringContent Json(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
