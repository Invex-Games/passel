using System.Net;
using System.Security.Claims;
using Invex.Passel.WebApi.Extensions;

namespace Invex.Passel.WebApi.Tests;

public sealed class RateLimitingTests
{
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
}
