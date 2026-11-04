using System.Net;
using System.Text.Json.Nodes;
using Invex.Passel.WebApi.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The device registry through the real API against a real database: registering, listing, renaming and
///     removing devices, and their push tokens. Push tokens are unique across all users, so every test makes up
///     tokens of its own.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class DeviceTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static string NewFcmToken() =>
        $"fcm-{Guid.NewGuid():N}:APA91b{Guid.NewGuid():N}";

    private static string NewPushEndpoint() =>
        $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

    [Fact]
    public async Task Requests_without_a_user_are_refused()
    {
        using var client = fixture.Api.CreateClient();

        using var list = await client.GetAsync(DeviceApi.DevicesPath, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        using var register = await client.PostDeviceAsync(
            DeviceApi.Registration(DeviceApi.NewInstallationId(), "Phone", "android"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
    }

    [Fact]
    public async Task A_registered_device_comes_back_with_an_id_and_without_a_push_token()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var before = DateTimeOffset.UtcNow;

        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(),
            "  Pixel 9  ",
            "android",
            HttpStatusCode.Created,
            CancellationToken);

        Assert.Equal("Pixel 9", device.Name);
        Assert.Equal("android", device.Platform);
        Assert.InRange(device.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(device.CreatedAt, device.LastSeenAt);
        Assert.Null(device.PushUpdatedAt);

        Assert.Equal([device], await alice.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task Registering_the_same_installation_again_returns_the_same_device_seen_again()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();

        var first = await alice.RegisterAsync(installationId, "Laptop", "extension", HttpStatusCode.Created, CancellationToken);
        await PacketApi.NextMillisecondAsync(CancellationToken);

        // The name sent the second time is not applied: a client sends its default name on every start, and the
        // user may have renamed the device in between
        var again = await alice.RegisterAsync(installationId, "Chrome on Windows", "extension", HttpStatusCode.OK, CancellationToken);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Laptop", again.Name);
        Assert.Equal(first.CreatedAt, again.CreatedAt);
        Assert.True(again.LastSeenAt > first.LastSeenAt);

        Assert.Equal([again], await alice.ListDevicesAsync(CancellationToken));
    }

    // Each round of a race lets one writer through, so this many racers would use up the store's three attempts if
    // the losers wrote again rather than take the winner's device
    private const int Racers = 10;

    [Fact]
    public async Task Registrations_of_one_installation_at_the_same_time_make_one_device()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();

        var (statuses, ids) = await RegisterAtOnceAsync(alice, installationId);

        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(Racers - 1, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], Assert.Single(await alice.ListDevicesAsync(CancellationToken)).Id);
    }

    [Fact]
    public async Task Registrations_of_a_registered_installation_at_the_same_time_all_find_its_device()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();

        var device = await alice.RegisterAsync(installationId, "Phone", "android", HttpStatusCode.Created, CancellationToken);
        await PacketApi.NextMillisecondAsync(CancellationToken);

        var (statuses, ids) = await RegisterAtOnceAsync(alice, installationId);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.All(ids, id => Assert.Equal(device.Id, id));
        Assert.True(Assert.Single(await alice.ListDevicesAsync(CancellationToken)).LastSeenAt > device.LastSeenAt);
    }

    private static async Task<(HttpStatusCode[] Statuses, string[] Ids)> RegisterAtOnceAsync(
        HttpClient client,
        string installationId)
    {
        var responses = await Task.WhenAll(Enumerable
            .Range(0, Racers)
            .Select(_ => client.PostDeviceAsync(DeviceApi.Registration(installationId, "Phone", "android"), CancellationToken)));

        var ids = new List<string>();

        foreach (var response in responses)
            using (response)
            {
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });

                using var document = await PacketApi.ReadJsonAsync(response, response.StatusCode, CancellationToken);
                ids.Add(DeviceJson.Parse(document.RootElement).Id);
            }

        return ([..responses.Select(response => response.StatusCode)], [..ids]);
    }

    [Fact]
    public async Task An_installation_registered_with_another_platform_is_a_conflict()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();

        await alice.RegisterAsync(installationId, "Browser", "web", HttpStatusCode.Created, CancellationToken);

        using var response = await alice.PostDeviceAsync(DeviceApi.Registration(installationId, "Browser", "extension"),
            CancellationToken);

        Assert.Contains("registered for the web app", await AssertProblemAsync(response, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Each_user_lists_only_their_own_devices_in_the_order_they_registered()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        var phone = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);
        await PacketApi.NextMillisecondAsync(CancellationToken);
        var laptop = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Laptop", "extension", HttpStatusCode.Created, CancellationToken);
        var bobs = await bob.RegisterAsync(DeviceApi.NewInstallationId(), "Bob's phone", "android", HttpStatusCode.Created, CancellationToken);

        Assert.Equal([phone, laptop], await alice.ListDevicesAsync(CancellationToken));
        Assert.Equal([bobs], await bob.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task One_installation_signed_in_as_two_users_is_a_device_of_each()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));
        var installationId = DeviceApi.NewInstallationId();

        var alices = await alice.RegisterAsync(installationId, "Shared phone", "android", HttpStatusCode.Created, CancellationToken);
        var bobs = await bob.RegisterAsync(installationId, "Shared phone", "android", HttpStatusCode.Created, CancellationToken);

        Assert.NotEqual(alices.Id, bobs.Id);
        Assert.Equal([alices], await alice.ListDevicesAsync(CancellationToken));
        Assert.Equal([bobs], await bob.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task A_renamed_device_keeps_its_new_name_when_it_registers_again()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();
        var device = await alice.RegisterAsync(installationId, "Pixel 9", "android", HttpStatusCode.Created, CancellationToken);

        using var response = await alice.RenameDeviceAsync(device.Id, " Work phone ", CancellationToken);
        using var renamed = await PacketApi.ReadJsonAsync(response, HttpStatusCode.OK, CancellationToken);

        Assert.Equal(device with { Name = "Work phone" }, DeviceJson.Parse(renamed.RootElement));

        var again = await alice.RegisterAsync(installationId, "Pixel 9", "android", HttpStatusCode.OK, CancellationToken);

        Assert.Equal("Work phone", again.Name);
    }

    [Fact]
    public async Task A_device_is_removed_once_and_its_installation_registers_again_as_a_new_device()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installationId = DeviceApi.NewInstallationId();
        var device = await alice.RegisterAsync(installationId, "Old laptop", "extension", HttpStatusCode.Created, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.RemoveDeviceAsync(device.Id, CancellationToken));
        Assert.Empty(await alice.ListDevicesAsync(CancellationToken));

        // Removing again finds nothing
        Assert.Equal(HttpStatusCode.NotFound, await alice.RemoveDeviceAsync(device.Id, CancellationToken));

        var registered = await alice.RegisterAsync(installationId, "Old laptop", "extension", HttpStatusCode.Created, CancellationToken);

        Assert.NotEqual(device.Id, registered.Id);
    }

    [Fact]
    public async Task Another_users_device_cannot_be_changed_nor_told_apart_from_a_missing_one()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        var alices = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await alice.PutPushAsync(alices.Id, DeviceApi.FcmToken(NewFcmToken()), CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var before = await alice.ListedAsync(alices.Id, CancellationToken);

        foreach (var id in new[] { alices.Id, "not-a-device-id" })
        {
            using (var rename = await bob.RenameDeviceAsync(id, "Mine now", CancellationToken))
                Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);

            using (var push = await bob.PutPushAsync(id, DeviceApi.FcmToken(NewFcmToken()), CancellationToken))
                Assert.Equal(HttpStatusCode.NotFound, push.StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, await bob.ClearPushAsync(id, CancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, await bob.RemoveDeviceAsync(id, CancellationToken));
        }

        Assert.Equal(before, await alice.ListedAsync(alices.Id, CancellationToken));
        Assert.Empty(await bob.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task A_push_token_is_set_replaced_and_cleared()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await alice.PutPushAsync(device.Id, DeviceApi.FcmToken(NewFcmToken()), CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var withToken = await alice.ListedAsync(device.Id, CancellationToken);

        Assert.NotNull(withToken.PushUpdatedAt);
        await PacketApi.NextMillisecondAsync(CancellationToken);

        using (var replace = await alice.PutPushAsync(device.Id, DeviceApi.FcmToken(NewFcmToken()), CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);

        var replaced = await alice.ListedAsync(device.Id, CancellationToken);

        Assert.True(replaced.PushUpdatedAt > withToken.PushUpdatedAt);

        Assert.Equal(HttpStatusCode.NoContent, await alice.ClearPushAsync(device.Id, CancellationToken));
        Assert.Null((await alice.ListedAsync(device.Id, CancellationToken)).PushUpdatedAt);

        // Clearing a device without a token is no error
        Assert.Equal(HttpStatusCode.NoContent, await alice.ClearPushAsync(device.Id, CancellationToken));
    }

    [Fact]
    public async Task A_push_token_registered_by_another_account_moves_to_it()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        // One phone, signed out of one account and into another: the installation stays, and so does its token
        var installationId = DeviceApi.NewInstallationId();
        var token = DeviceApi.FcmToken(NewFcmToken());

        var alicesPhone = await alice.RegisterAsync(installationId, "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await alice.PutPushAsync(alicesPhone.Id, token, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var bobsPhone = await bob.RegisterAsync(installationId, "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await bob.PutPushAsync(bobsPhone.Id, token, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        Assert.Null((await alice.ListedAsync(alicesPhone.Id, CancellationToken)).PushUpdatedAt);
        Assert.NotNull((await bob.ListedAsync(bobsPhone.Id, CancellationToken)).PushUpdatedAt);

        // A browser subscription moves the same way, matched on its endpoint whatever its keys
        var endpoint = NewPushEndpoint();
        var alicesBrowser = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Browser", "web", HttpStatusCode.Created, CancellationToken);
        var bobsBrowser = await bob.RegisterAsync(DeviceApi.NewInstallationId(), "Browser", "extension", HttpStatusCode.Created, CancellationToken);

        using (var set = await alice.PutPushAsync(alicesBrowser.Id, DeviceApi.WebPushSubscription(endpoint), CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var resubscribed = DeviceApi.WebPushSubscription(endpoint);
        resubscribed["keys"]!["auth"] = "AAAAAAAAAAAAAAAAAAAAAA";

        using (var set = await bob.PutPushAsync(bobsBrowser.Id, resubscribed, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        Assert.Null((await alice.ListedAsync(alicesBrowser.Id, CancellationToken)).PushUpdatedAt);
        Assert.NotNull((await bob.ListedAsync(bobsBrowser.Id, CancellationToken)).PushUpdatedAt);
    }

    [Fact]
    public async Task A_push_token_moves_between_one_users_devices_and_a_replaced_one_is_let_go()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));
        var first = DeviceApi.FcmToken(NewFcmToken());
        var second = DeviceApi.FcmToken(NewFcmToken());

        var oldPhone = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Old phone", "android", HttpStatusCode.Created, CancellationToken);
        var newPhone = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "New phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await alice.PutPushAsync(oldPhone.Id, first, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        using (var set = await alice.PutPushAsync(newPhone.Id, first, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        Assert.Null((await alice.ListedAsync(oldPhone.Id, CancellationToken)).PushUpdatedAt);
        Assert.NotNull((await alice.ListedAsync(newPhone.Id, CancellationToken)).PushUpdatedAt);

        // The new phone replaces its token; the first one is then nobody's, and taking it moves nothing away
        using (var replace = await alice.PutPushAsync(newPhone.Id, second, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);

        var bobsPhone = await bob.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var set = await bob.PutPushAsync(bobsPhone.Id, first, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        Assert.NotNull((await alice.ListedAsync(newPhone.Id, CancellationToken)).PushUpdatedAt);

        // Setting the token a device already holds only refreshes it
        using (var again = await bob.PutPushAsync(bobsPhone.Id, first, CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        Assert.NotNull((await bob.ListedAsync(bobsPhone.Id, CancellationToken)).PushUpdatedAt);
    }

    [Fact]
    public async Task A_push_token_of_the_kind_another_platform_uses_is_refused()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        var phone = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);
        var browser = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Browser", "extension", HttpStatusCode.Created, CancellationToken);

        using (var response = await alice.PutPushAsync(phone.Id, DeviceApi.WebPushSubscription(NewPushEndpoint()), CancellationToken))
            await AssertValidationProblemAsync(response, "$type");

        using (var response = await alice.PutPushAsync(browser.Id, DeviceApi.FcmToken(NewFcmToken()), CancellationToken))
            await AssertValidationProblemAsync(response, "$type");

        Assert.All(await alice.ListDevicesAsync(CancellationToken), device => Assert.Null(device.PushUpdatedAt));
    }

    [Fact]
    public async Task A_user_at_the_device_cap_cannot_register_another_until_one_is_removed()
    {
        var cap = fixture.Api.Services.GetRequiredService<IOptions<QuotaOptions>>().Value.MaxDevicesPerUser;
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var installations = new List<string>();
        var devices = new List<DeviceJson>();

        for (var i = 0; i < cap; i++)
        {
            installations.Add(DeviceApi.NewInstallationId());
            devices.Add(await alice.RegisterAsync(installations[^1], $"Device {i}", "web", HttpStatusCode.Created, CancellationToken));
        }

        using (var response = await alice.PostDeviceAsync(DeviceApi.Registration(DeviceApi.NewInstallationId(), "One more", "web"),
                   CancellationToken))
        {
            // Not the rate limit: waiting changes nothing, so there is no Retry-After
            Assert.Contains($"{cap} devices allowed", await AssertProblemAsync(response, HttpStatusCode.TooManyRequests));
            Assert.False(response.Headers.Contains("Retry-After"));
        }

        // A device that is registered already is never refused
        await alice.RegisterAsync(installations[0], "Device 0", "web", HttpStatusCode.OK, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.RemoveDeviceAsync(devices[^1].Id, CancellationToken));
        await alice.RegisterAsync(DeviceApi.NewInstallationId(), "One more", "web", HttpStatusCode.Created, CancellationToken);

        Assert.Equal(cap, (await alice.ListDevicesAsync(CancellationToken)).Count);
    }

    public static TheoryData<string, string> InvalidRegistrations =>
        new()
        {
            { """{ "installationId": "too-short", "name": "Phone", "platform": "android" }""", "installationId" },
            { """{ "installationId": "6f1c2a9e 4b7d 4e2a 9c3f 0d8e5b7a1c24", "name": "Phone", "platform": "android" }""", "installationId" },
            { $$"""{ "installationId": "{{new string('a', 129)}}", "name": "Phone", "platform": "android" }""", "installationId" },
            { """{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": "   ", "platform": "android" }""", "name" },
            { """{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": null, "platform": "android" }""", "name" },
            { $$"""{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": "{{new string('a', 65)}}", "platform": "android" }""", "name" },
            { """{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": "Tab\there", "platform": "android" }""", "name" },
            { """{ "installationId": "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24", "name": "Phone", "platform": 7 }""", "platform" },
        };

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task An_invalid_registration_is_refused_with_a_validation_problem(string body, string field)
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        using var response = await alice.PostDeviceAsync(JsonNode.Parse(body)!.AsObject(), CancellationToken);

        await AssertValidationProblemAsync(response, field);
        Assert.Empty(await alice.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task A_registration_the_API_cannot_read_is_refused_with_a_problem()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        // An unknown platform, and a body without one, fail before validation, in every environment the same way
        using (var response = await alice.PostDeviceAsync(DeviceApi.Registration(DeviceApi.NewInstallationId(), "iPhone", "ios"),
                   CancellationToken))
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        using (var response = await alice.PostDeviceAsync(new JsonObject
               {
                   ["installationId"] = DeviceApi.NewInstallationId(),
                   ["name"] = "Phone",
               },
               CancellationToken))
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        Assert.Empty(await alice.ListDevicesAsync(CancellationToken));
    }

    [Fact]
    public async Task An_invalid_name_is_refused_on_rename()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Phone", "android", HttpStatusCode.Created, CancellationToken);

        using (var response = await alice.RenameDeviceAsync(device.Id, "", CancellationToken))
            await AssertValidationProblemAsync(response, "name");

        using (var response = await alice.RenameDeviceAsync(device.Id, new string('a', 65), CancellationToken))
            await AssertValidationProblemAsync(response, "name");

        Assert.Equal("Phone", (await alice.ListedAsync(device.Id, CancellationToken)).Name);
    }

    public static TheoryData<string, string> InvalidPushTokens =>
        new()
        {
            { """{ "$type": "Fcm", "token": "" }""", "token" },
            { """{ "$type": "Fcm", "token": "has a space" }""", "token" },
            { $$"""{ "$type": "Fcm", "token": "{{new string('a', 4097)}}" }""", "token" },
            { """{ "$type": "WebPush", "endpoint": "http://push.example.com/abc", "keys": { "p256dh": "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "auth": "tBHItJI5svbpez7KI4CCXg" } }""", "endpoint" },
            { """{ "$type": "WebPush", "endpoint": "https://10.0.0.1/abc", "keys": { "p256dh": "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "auth": "tBHItJI5svbpez7KI4CCXg" } }""", "endpoint" },
            { """{ "$type": "WebPush", "endpoint": "https://push.example.com:8443/abc", "keys": { "p256dh": "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "auth": "tBHItJI5svbpez7KI4CCXg" } }""", "endpoint" },
            { """{ "$type": "WebPush", "endpoint": "https://push.example.com/abc", "keys": null }""", "keys" },
            { """{ "$type": "WebPush", "endpoint": "https://push.example.com/abc", "keys": { "p256dh": "not base64url!", "auth": "tBHItJI5svbpez7KI4CCXg" } }""", "keys.p256dh" },
            { """{ "$type": "WebPush", "endpoint": "https://push.example.com/abc", "keys": { "p256dh": "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "auth": "dG9vIHNob3J0" } }""", "keys.auth" },
        };

    [Theory]
    [MemberData(nameof(InvalidPushTokens))]
    public async Task An_invalid_push_token_is_refused_with_a_validation_problem(string body, string field)
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var platform = body.Contains("WebPush") ? "web" : "android";
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Device", platform, HttpStatusCode.Created, CancellationToken);

        using var response = await alice.PutPushAsync(device.Id, JsonNode.Parse(body)!.AsObject(), CancellationToken);

        await AssertValidationProblemAsync(response, field);
        Assert.Null((await alice.ListedAsync(device.Id, CancellationToken)).PushUpdatedAt);
    }

    [Fact]
    public async Task A_push_token_the_API_cannot_read_is_refused_with_a_problem()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var device = await alice.RegisterAsync(DeviceApi.NewInstallationId(), "Browser", "web", HttpStatusCode.Created, CancellationToken);

        // PushSubscription.toJSON() as the browser gives it, without the discriminator, and an unknown kind
        foreach (var body in new[]
                 {
                     $$"""{ "endpoint": "{{NewPushEndpoint()}}", "expirationTime": null, "keys": { "p256dh": "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "auth": "tBHItJI5svbpez7KI4CCXg" } }""",
                     """{ "$type": "Apns", "token": "abc" }""",
                 })
            using (var response = await alice.PutPushAsync(device.Id, JsonNode.Parse(body)!.AsObject(), CancellationToken))
                await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        Assert.Null((await alice.ListedAsync(device.Id, CancellationToken)).PushUpdatedAt);
    }

    /// <summary>
    ///     The response is a problem with the expected status; returns its <c>detail</c>.
    /// </summary>
    private static async Task<string?> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using var problem = await PacketApi.ReadJsonAsync(response, expected, CancellationToken);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());

        return problem.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString()
            : null;
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string field)
    {
        using var problem = await PacketApi.ReadJsonAsync(response, HttpStatusCode.BadRequest, CancellationToken);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = problem.RootElement.GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"The problem names {errors} rather than {field}.");
    }
}
