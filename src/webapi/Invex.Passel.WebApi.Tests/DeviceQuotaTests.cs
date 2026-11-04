using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;

namespace Invex.Passel.WebApi.Tests;

public sealed class DeviceQuotaTests
{
    private static readonly QuotaOptions Quotas = new()
    {
        MaxDevicesPerUser = 3,
    };

    [Fact]
    public void A_user_below_the_cap_may_register_another_device()
    {
        Assert.Null(DeviceQuota.Exceeded(2, Quotas));
    }

    [Fact]
    public void A_user_at_the_cap_is_refused_with_429_and_told_to_remove_one()
    {
        var exceeded = DeviceQuota.Exceeded(3, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, exceeded.StatusCode);
        Assert.Equal("Device limit reached", exceeded.Title);
        Assert.Contains("3 of the 3 devices allowed", exceeded.Detail);
        Assert.Contains("Remove one", exceeded.Detail);
    }

    [Fact]
    public void The_default_cap_is_twenty_devices()
    {
        var defaults = new QuotaOptions();

        Assert.Null(DeviceQuota.Exceeded(19, defaults));
        Assert.Contains("20 devices allowed", DeviceQuota.Exceeded(20, defaults)?.Detail);
    }
}
