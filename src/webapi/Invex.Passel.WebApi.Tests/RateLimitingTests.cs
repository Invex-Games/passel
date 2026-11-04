using System.Net;
using System.Security.Claims;
using Invex.Passel.WebApi.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.Tests;

public sealed class RateLimitingTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void PartitionKey_counts_signed_in_users_by_their_id()
    {
        var user = Principal(new Claim(ClaimTypes.NameIdentifier, "1234567890"));

        var key = RateLimitingExtensions.PartitionKey(user, IPAddress.Parse("203.0.113.7"));

        Assert.Equal("user:1234567890", key);
    }

    [Fact]
    public void PartitionKey_counts_anonymous_requests_by_address()
    {
        var key = RateLimitingExtensions.PartitionKey(new ClaimsPrincipal(), IPAddress.Parse("203.0.113.7"));

        Assert.Equal("ip:203.0.113.7", key);
    }

    [Fact]
    public void PartitionKey_ignores_a_blank_user_id()
    {
        var user = Principal(new Claim(ClaimTypes.NameIdentifier, ""));

        var key = RateLimitingExtensions.PartitionKey(user, IPAddress.IPv6Loopback);

        Assert.Equal("ip:::1", key);
    }

    [Fact]
    public void PartitionKey_groups_requests_without_an_address()
    {
        var key = RateLimitingExtensions.PartitionKey(new ClaimsPrincipal(), null);

        Assert.Equal("ip:unknown", key);
    }

    /// <summary>
    ///     An endpoint that names a policy nobody registered fails every request, so each parcel policy is exercised
    ///     through a host with the API's registration: at a limit of one, the second request in the minute is refused.
    /// </summary>
    [Theory]
    [InlineData(RateLimitPolicies.ParcelUploads, "RateLimits:ParcelUploadsPerMinute")]
    [InlineData(RateLimitPolicies.ParcelReads, "RateLimits:ParcelReadsPerMinute")]
    public async Task The_parcel_policies_are_registered_with_their_own_limits(string policy, string setting)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        // The API's appsettings.json is copied next to the tests, and its AllowedHosts would answer 400 to the test
        // server's host
        builder.Configuration["AllowedHosts"] = "*";
        builder.Configuration[setting] = "1";

        builder.Services.AddProblemDetails();
        builder.AddPacketRateLimiting();

        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/limited", () => Results.Ok()).RequireRateLimiting(policy);

        await app.StartAsync(CancellationToken);
        using var client = app.GetTestClient();

        using var first = await client.GetAsync("/limited", CancellationToken);
        using var second = await client.GetAsync("/limited", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }
}
