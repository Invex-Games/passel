using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     The checks on what clients send to the device endpoints. That a refused request answers with a validation
///     problem and stores nothing is covered by the integration tests.
/// </summary>
public sealed class DeviceValidationTests
{
    private const string InstallationId = "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24";

    /// <summary>
    ///     RFC 8291's example subscription keys.
    /// </summary>
    private const string P256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM";

    private const string Auth = "tBHItJI5svbpez7KI4CCXg";

    private static DeviceEndpoints.RegisterDeviceRequestBody Registration(
        string installationId = InstallationId,
        string name = "Pixel 9",
        DevicePlatform platform = DevicePlatform.Android) =>
        new()
        {
            InstallationId = installationId,
            Name = name,
            Platform = platform,
        };

    private static DeviceEndpoints.PutDevicePushRequestBody.WebPush Subscription(
        string endpoint = "https://fcm.googleapis.com/fcm/send/abc",
        string p256dh = P256dh,
        string auth = Auth) =>
        new()
        {
            Endpoint = endpoint,
            Keys = new()
            {
                P256dh = p256dh,
                Auth = auth,
            },
        };

    [Theory]
    [InlineData(InstallationId)]
    [InlineData("6f1c2a9e4b7d4e2a9c3f0d8e5b7a1c24")]
    [InlineData("b64url_ID-of-some-length")]
    public void A_registration_with_a_usual_installation_id_is_accepted(string installationId)
    {
        Assert.Empty(DeviceEndpoints.Validate(Registration(installationId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short-id")]
    [InlineData("6f1c2a9e 4b7d 4e2a 9c3f")]
    [InlineData("6f1c2a9e/4b7d/4e2a/9c3f")]
    [InlineData("6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c2é")]
    public void An_installation_id_that_is_short_or_needs_escaping_is_refused(string installationId)
    {
        Assert.Contains("installationId", DeviceEndpoints.Validate(Registration(installationId)).Keys);
    }

    [Fact]
    public void An_installation_id_is_at_most_128_characters()
    {
        Assert.Empty(DeviceEndpoints.Validate(Registration(new('a', DeviceEndpoints.MaxInstallationIdLength))));
        Assert.Contains("installationId",
            DeviceEndpoints.Validate(Registration(new('a', DeviceEndpoints.MaxInstallationIdLength + 1))).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    [InlineData("Null\0char")]
    public void A_blank_name_or_one_with_control_characters_is_refused(string name)
    {
        Assert.Contains("name", DeviceEndpoints.Validate(Registration(name: name)).Keys);
        Assert.Contains("name", DeviceEndpoints.Validate(new DeviceEndpoints.RenameDeviceRequestBody { Name = name }).Keys);
    }

    [Fact]
    public void A_name_is_measured_without_the_whitespace_around_it()
    {
        var longest = new string('a', DeviceEndpoints.MaxNameLength);

        Assert.Empty(DeviceEndpoints.Validate(Registration(name: $"  {longest}  ")));
        Assert.Contains("name", DeviceEndpoints.Validate(Registration(name: longest + "a")).Keys);
        Assert.Equal("Work phone", DeviceEndpoints.NormalizeName("  Work phone \t"));
    }

    [Fact]
    public void A_missing_name_is_refused()
    {
        // The JSON options let an explicit null through to a non-nullable property
        Assert.Contains("name", DeviceEndpoints.Validate(Registration(name: null!)).Keys);
    }

    [Fact]
    public void A_platform_number_without_a_name_is_refused()
    {
        Assert.Contains("platform", DeviceEndpoints.Validate(Registration(platform: (DevicePlatform)7)).Keys);
    }

    [Theory]
    [InlineData(DevicePlatform.Android, "registered for the Android app.")]
    [InlineData(DevicePlatform.Extension, "registered for the browser extension.")]
    [InlineData(DevicePlatform.Web, "registered for the web app.")]
    public void A_platform_mismatch_names_the_client_the_installation_is_registered_for(
        DevicePlatform registeredPlatform,
        string expected)
    {
        Assert.Contains(expected, DeviceEndpoints.PlatformMismatchDetail(registeredPlatform));
    }

    [Fact]
    public void Every_platform_has_a_platform_mismatch_detail()
    {
        foreach (var platform in Enum.GetValues<DevicePlatform>())
            Assert.NotEmpty(DeviceEndpoints.PlatformMismatchDetail(platform));
    }

    [Fact]
    public void An_fcm_token_is_printable_ascii_without_spaces()
    {
        Assert.Empty(DeviceEndpoints.Validate(new DeviceEndpoints.PutDevicePushRequestBody.Fcm
        {
            Token = "dQw4w9WgXcQ:APA91bH-example_token.with~punctuation",
        }));

        foreach (var token in new[] { "", "has space", "tab\there", "naïve", new string('a', DeviceEndpoints.MaxFcmTokenLength + 1) })
            Assert.Contains("token", DeviceEndpoints.Validate(new DeviceEndpoints.PutDevicePushRequestBody.Fcm { Token = token }).Keys);
    }

    [Fact]
    public void A_browser_subscription_is_accepted()
    {
        Assert.Empty(DeviceEndpoints.Validate(Subscription()));
        Assert.Empty(DeviceEndpoints.Validate(Subscription("https://updates.push.services.mozilla.com/wpush/v2/gAAAAABk")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/fcm/send/abc")]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://203.0.113.7/send/abc")]
    [InlineData("https://[2001:db8::1]/send/abc")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]
    [InlineData("https://user:secret@fcm.googleapis.com/fcm/send/abc")]
    [InlineData("ftp://fcm.googleapis.com/fcm/send/abc")]
    public void A_push_endpoint_that_is_not_https_to_a_named_host_is_refused(string endpoint)
    {
        Assert.Contains("endpoint", DeviceEndpoints.Validate(Subscription(endpoint)).Keys);
    }

    [Fact]
    public void A_push_endpoint_is_at_most_2048_characters()
    {
        const string prefix = "https://fcm.googleapis.com/fcm/send/";

        Assert.Empty(DeviceEndpoints.Validate(Subscription(prefix + new string('a', DeviceEndpoints.MaxPushEndpointLength - prefix.Length))));
        Assert.Contains("endpoint",
            DeviceEndpoints.Validate(Subscription(prefix + new string('a', DeviceEndpoints.MaxPushEndpointLength - prefix.Length + 1))).Keys);
    }

    [Theory]
    // Not base64url, the standard alphabet, too short, and a compressed point (0x02) of the right length
    [InlineData("not base64url!")]
    [InlineData("BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA/0QTpQtUbVlUls0VJXg7A8u+Ts1XbjhazAkj7I99e8QcYP7DkM")]
    [InlineData("BNcRdreALRFXTkOOUHK1")]
    [InlineData("AtcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM")]
    public void A_p256dh_key_that_is_not_an_uncompressed_point_is_refused(string p256dh)
    {
        Assert.Contains("keys.p256dh", DeviceEndpoints.Validate(Subscription(p256dh: p256dh)).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dG9vIHNob3J0")]
    [InlineData("tBHItJI5svbpez7KI4CCXgAA")]
    public void An_auth_secret_that_is_not_16_bytes_is_refused(string auth)
    {
        Assert.Contains("keys.auth", DeviceEndpoints.Validate(Subscription(auth: auth)).Keys);
    }

    [Fact]
    public void A_subscription_without_keys_is_refused()
    {
        var errors = DeviceEndpoints.Validate(new DeviceEndpoints.PutDevicePushRequestBody.WebPush
        {
            Endpoint = "https://fcm.googleapis.com/fcm/send/abc",
            Keys = null!,
        });

        Assert.Equal(["keys"], errors.Keys);
    }

    [Fact]
    public void No_message_repeats_the_token()
    {
        const string token = "secret token";

        var errors = DeviceEndpoints.Validate(new DeviceEndpoints.PutDevicePushRequestBody.Fcm { Token = token });

        Assert.DoesNotContain(errors.Values.SelectMany(messages => messages), message => message.Contains(token));
    }
}
