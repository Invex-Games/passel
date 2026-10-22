using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;

namespace Invex.Passel.WebApi.Tests;

public sealed class PacketQuotaTests
{
    private static readonly QuotaOptions Quotas = new()
    {
        MaxPacketsPerUser = 3,
        MaxBytesPerUser = 1000,
    };

    [Fact]
    public void A_packet_that_reaches_both_limits_exactly_still_fits()
    {
        var exceeded = PacketQuota.Exceeded(new(2, 500), 500, Quotas);

        Assert.Null(exceeded);
    }

    [Fact]
    public void The_packet_limit_is_refused_with_429()
    {
        var exceeded = PacketQuota.Exceeded(new(3, 0), 1, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, exceeded.StatusCode);
        Assert.Contains("3", exceeded.Detail);
    }

    [Fact]
    public void A_packet_that_would_pass_the_byte_limit_is_refused_with_413()
    {
        var exceeded = PacketQuota.Exceeded(new(0, 900), 101, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exceeded.StatusCode);
    }

    [Fact]
    public void The_packet_count_is_checked_before_the_bytes()
    {
        // Deleting packets fixes both, so that is the advice to give
        var exceeded = PacketQuota.Exceeded(new(3, 999), 100, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, exceeded.StatusCode);
    }

    [Fact]
    public void The_defaults_are_described_in_readable_units()
    {
        var defaults = new QuotaOptions();

        var packets = PacketQuota.Exceeded(new(defaults.MaxPacketsPerUser, 0), 1, defaults);
        var bytes = PacketQuota.Exceeded(new(0, defaults.MaxBytesPerUser), 1, defaults);

        Assert.Contains("5,000 packets", packets?.Detail);
        Assert.Contains("50 MB", bytes?.Detail);
    }
}
