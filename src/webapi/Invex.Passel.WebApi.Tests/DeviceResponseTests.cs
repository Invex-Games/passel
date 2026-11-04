using System.Text.Json;
using System.Text.Json.Serialization;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.DeviceEndpoints;
using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Configuration;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The mapping from a stored device to its wire shape, which never carries the push token.
/// </summary>
public sealed class DeviceResponseTests
{
    private static readonly IdEncodingService Encoder = new(new ConfigurationBuilder().Build());

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    ///     What matters of the host's JSON options here: the web defaults, and enum names in camel case.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static Device Stored() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            InstallationId = "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24",
            Name = "Pixel 9",
            Platform = DevicePlatform.Android,
            CreatedAt = CreatedAt,
            LastSeenAt = CreatedAt.AddDays(3),
        };

    [Fact]
    public void A_device_carries_its_public_id_its_name_its_platform_and_its_times()
    {
        var device = Stored();

        var response = DeviceEndpoints.DeviceResponse.From(device, Encoder);

        Assert.Equal(Encoder.Encode(device.Id), response.Id);
        Assert.Equal("Pixel 9", response.Name);
        Assert.Equal(DevicePlatform.Android, response.Platform);
        Assert.Equal(CreatedAt, response.CreatedAt);
        Assert.Equal(CreatedAt.AddDays(3), response.LastSeenAt);
        Assert.Null(response.PushUpdatedAt);
    }

    [Fact]
    public void A_device_with_a_push_token_says_when_it_was_set_and_nothing_more()
    {
        const string token = "fcm-secret-token";

        var device = Stored().WithPush(new DevicePush.Fcm
        {
            Token = token,
            UpdatedAt = CreatedAt.AddHours(1),
        });

        var response = DeviceEndpoints.DeviceResponse.From(device, Encoder);
        var json = JsonSerializer.Serialize(response, WireOptions);

        Assert.Equal(CreatedAt.AddHours(1), response.PushUpdatedAt);
        Assert.DoesNotContain(token, json);
        Assert.DoesNotContain(device.PushKey!, json);
        Assert.DoesNotContain(device.InstallationId, json);

        Assert.Equal(
            $$"""{"id":"{{response.Id}}","name":"Pixel 9","platform":"android","createdAt":"2026-11-04T09:30:00+00:00","lastSeenAt":"2026-11-07T09:30:00+00:00","pushUpdatedAt":"2026-11-04T10:30:00+00:00"}""",
            json);
    }
}
