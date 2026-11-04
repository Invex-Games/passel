using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     Push tokens as the API stores them: the key that makes a token unique across users, which platform takes
///     which kind, and that nothing prints a token. The cross-user move itself is covered by the integration tests.
/// </summary>
public sealed class DevicePushTests
{
    private const string P256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM";

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    private static DevicePush.Fcm Fcm(string token) =>
        new()
        {
            Token = token,
            UpdatedAt = Now,
        };

    private static DevicePush.WebPush WebPush(string endpoint, string auth = "tBHItJI5svbpez7KI4CCXg") =>
        new()
        {
            Endpoint = endpoint,
            P256dh = P256dh,
            Auth = auth,
            UpdatedAt = Now,
        };

    private static Device Device(DevicePlatform platform) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "user",
            InstallationId = "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24",
            Name = "Device",
            Platform = platform,
            CreatedAt = Now,
            LastSeenAt = Now,
        };

    [Fact]
    public void The_key_is_the_same_for_the_same_token_whenever_it_was_sent()
    {
        Assert.Equal(DevicePush.KeyOf(Fcm("token")), DevicePush.KeyOf(Fcm("token") with { UpdatedAt = Now.AddDays(1) }));
        Assert.NotEqual(DevicePush.KeyOf(Fcm("token")), DevicePush.KeyOf(Fcm("other token")));
    }

    [Fact]
    public void A_subscription_is_keyed_by_its_endpoint_alone()
    {
        // A browser that subscribes again may get new keys for the same endpoint; it is still the same channel
        Assert.Equal(DevicePush.KeyOf(WebPush("https://push.example.com/a")),
            DevicePush.KeyOf(WebPush("https://push.example.com/a", auth: "AAAAAAAAAAAAAAAAAAAAAA")));
        Assert.NotEqual(DevicePush.KeyOf(WebPush("https://push.example.com/a")),
            DevicePush.KeyOf(WebPush("https://push.example.com/b")));
    }

    [Fact]
    public void The_key_is_a_fixed_length_hash_that_does_not_hold_the_token()
    {
        var key = DevicePush.KeyOf(Fcm(new string('x', 4096)));

        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void The_two_kinds_never_share_a_key()
    {
        const string same = "https://push.example.com/a";

        Assert.NotEqual(DevicePush.KeyOf(Fcm(same)), DevicePush.KeyOf(WebPush(same)));
    }

    [Fact]
    public void WithPush_keeps_the_key_in_step_with_the_token()
    {
        var device = Device(DevicePlatform.Android).WithPush(Fcm("token"));

        Assert.Equal(DevicePush.KeyOf(Fcm("token")), device.PushKey);

        var cleared = device.WithPush(null);

        Assert.Null(cleared.Push);
        Assert.Null(cleared.PushKey);
    }

    [Theory]
    [InlineData(DevicePlatform.Android, true, false)]
    [InlineData(DevicePlatform.Extension, false, true)]
    [InlineData(DevicePlatform.Web, false, true)]
    public void Android_takes_fcm_tokens_and_browsers_take_subscriptions(DevicePlatform platform, bool fcm, bool webPush)
    {
        Assert.Equal(fcm, DeviceStoreService.Accepts(platform, Fcm("token")));
        Assert.Equal(webPush, DeviceStoreService.Accepts(platform, WebPush("https://push.example.com/a")));
    }

    [Fact]
    public void A_request_becomes_the_stored_token_stamped_with_the_time()
    {
        var fcm = DeviceEndpoints.ToPush(new DeviceEndpoints.PutDevicePushRequestBody.Fcm
            {
                Token = "token",
            },
            Now);

        Assert.Equal(Fcm("token"), Assert.IsType<DevicePush.Fcm>(fcm));

        var webPush = DeviceEndpoints.ToPush(new DeviceEndpoints.PutDevicePushRequestBody.WebPush
            {
                Endpoint = "https://push.example.com/a",
                Keys = new()
                {
                    P256dh = P256dh,
                    Auth = "tBHItJI5svbpez7KI4CCXg",
                },
            },
            Now);

        Assert.Equal(WebPush("https://push.example.com/a"), Assert.IsType<DevicePush.WebPush>(webPush));
    }

    [Fact]
    public void Printing_a_token_or_a_device_that_holds_one_never_shows_it()
    {
        const string token = "fcm-secret-token";
        const string auth = "c2VjcmV0LWF1dGgtc2VjcmV0";

        var printed = new object[]
        {
            Fcm(token),
            WebPush($"https://push.example.com/{token}", auth),
            Device(DevicePlatform.Android).WithPush(Fcm(token)),
            new DeviceEndpoints.PutDevicePushRequestBody.Fcm { Token = token },
            new DeviceEndpoints.WebPushKeys { P256dh = token, Auth = auth },
        };

        foreach (var value in printed)
        {
            var text = value.ToString()!;

            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain(auth, text);
        }
    }
}
