using Invex.Passel.WebApi.Authentication;
using Invex.Passel.WebApi.Extensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Primitives;

namespace Invex.Passel.WebApi.Tests;

public sealed class DevAuthenticationTests
{
    [Theory]
    [InlineData("Dev alice", "alice")]
    [InlineData("dev alice", "alice")]
    [InlineData("Dev  alice ", "alice")]
    [InlineData("Dev 1234567890", "1234567890")]
    [InlineData("Dev alice.smith+dev@example.com", "alice.smith+dev@example.com")]
    public void TryParseUserId_reads_the_user_id(string header, string expected)
    {
        Assert.True(DevAuthenticationHandler.TryParseUserId(header, out var userId));
        Assert.Equal(expected, userId);
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Dev ")]
    [InlineData("Dev alice bob")]
    [InlineData("Dev al\"ice")]
    [InlineData("Dev ../alice")]
    [InlineData("Developer alice")]
    [InlineData("Bearer alice")]
    [InlineData("")]
    public void TryParseUserId_rejects_other_headers_and_bad_ids(string header)
    {
        Assert.False(DevAuthenticationHandler.TryParseUserId(header, out var userId));
        Assert.Null(userId);
    }

    [Fact]
    public void TryParseUserId_limits_the_length_to_a_google_sub()
    {
        var longest = new string('a', DevAuthenticationHandler.MaxUserIdLength);

        Assert.True(DevAuthenticationHandler.TryParseUserId($"Dev {longest}", out _));
        Assert.False(DevAuthenticationHandler.TryParseUserId($"Dev {longest}a", out _));
    }

    [Fact]
    public void TryParseUserId_rejects_more_than_one_header()
    {
        var headers = new StringValues(["Dev alice", "Dev bob"]);

        Assert.False(DevAuthenticationHandler.IsDevAuthorization(headers));
        Assert.False(DevAuthenticationHandler.TryParseUserId(headers, out _));
    }

    [Fact]
    public void IsDevAuthorization_claims_the_header_even_with_a_bad_user_id()
    {
        // So the Dev scheme answers with its own 401 rather than handing the request to the bearer handler
        Assert.True(DevAuthenticationHandler.IsDevAuthorization("Dev alice bob"));
        Assert.False(DevAuthenticationHandler.IsDevAuthorization("Bearer token"));
    }

    [Theory]
    [InlineData("Dev alice", true, DevAuthenticationHandler.SchemeName)]
    [InlineData("Dev alice", false, DevAuthenticationHandler.SchemeName)]
    [InlineData("Bearer token", true, JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("Bearer token", false, JwtBearerDefaults.AuthenticationScheme)]
    [InlineData(null, true, CookieAuthenticationDefaults.AuthenticationScheme)]
    [InlineData(null, false, DevAuthenticationHandler.SchemeName)]
    public void SelectDevelopmentScheme_routes_by_header_and_google_setup(
        string? header,
        bool googleLoginConfigured,
        string expected)
    {
        var authorization = header is null
            ? StringValues.Empty
            : new StringValues(header);

        Assert.Equal(expected, HostWebExtensions.SelectDevelopmentScheme(authorization, googleLoginConfigured));
    }

    [Fact]
    public void ValidAudiences_skips_blank_values()
    {
        var audiences = HostWebExtensions.ValidAudiences(null, ["", " ", "extension-client-id"]);

        Assert.Equal(["extension-client-id"], audiences);
    }

    [Fact]
    public void ValidAudiences_puts_the_web_client_first()
    {
        var audiences = HostWebExtensions.ValidAudiences("web-client-id", ["extension-client-id"]);

        Assert.Equal(["web-client-id", "extension-client-id"], audiences);
    }
}
