using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;
using Microsoft.AspNetCore.Http;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelQuotaTests
{
    private const long Megabyte = 1024 * 1024;

    private static readonly QuotaOptions Quotas = new()
    {
        MaxPendingParcelsPerUser = 3,
        MaxParcelBytesPerUser = 10 * Megabyte,
    };

    [Fact]
    public void A_file_that_reaches_the_byte_limit_exactly_still_fits_beside_files_still_unsent()
    {
        Assert.Null(ParcelQuota.Exceeded(new(2, 6 * Megabyte), 4 * Megabyte, Quotas));
    }

    [Fact]
    public void An_upload_with_as_many_files_unsent_as_allowed_is_refused_with_429()
    {
        var exceeded = ParcelQuota.Exceeded(new(3, 0), 1, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, exceeded.StatusCode);
        Assert.Equal("Too many unsent files", exceeded.Title);
        Assert.Equal(
            "You have 3 files uploaded but not sent. Send or delete them, or wait an hour for them to expire.",
            exceeded.Detail);
    }

    [Fact]
    public void A_file_one_byte_past_the_byte_limit_is_refused_with_413()
    {
        var exceeded = ParcelQuota.Exceeded(new(0, 6 * Megabyte), 4 * Megabyte + 1, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exceeded.StatusCode);
        Assert.Equal("Storage limit reached", exceeded.Title);
        Assert.Equal("Your files use 6 MB of 10 MB. Delete some files and try again.", exceeded.Detail);
    }

    /// <summary>
    ///     A user at both limits is told to deal with what is waiting to be sent, which frees storage too.
    /// </summary>
    [Fact]
    public void The_unsent_count_is_checked_before_the_bytes()
    {
        var exceeded = ParcelQuota.Exceeded(new(3, 10 * Megabyte), Megabyte, Quotas);

        Assert.NotNull(exceeded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, exceeded.StatusCode);
    }

    [Fact]
    public void Counts_and_sizes_are_written_with_thousands_separators_whatever_the_culture()
    {
        var quotas = new QuotaOptions
        {
            MaxPendingParcelsPerUser = 1000,
            MaxParcelBytesPerUser = 2000 * Megabyte,
        };

        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new("de-DE");

        try
        {
            Assert.Contains("You have 1,000 files",
                ParcelQuota.Exceeded(new(1000, 0), 1, quotas)?.Detail);
            Assert.Contains("use 1,500 MB of 2,000 MB",
                ParcelQuota.Exceeded(new(0, 1500 * Megabyte), 600 * Megabyte, quotas)?.Detail);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Usage_under_a_megabyte_is_given_in_kilobytes()
    {
        var exceeded = ParcelQuota.Exceeded(new(0, 512 * 1024), 10 * Megabyte, Quotas);

        Assert.Equal("Your files use 512 KB of 10 MB. Delete some files and try again.", exceeded?.Detail);
    }

    [Theory]
    [InlineData(25 * Megabyte, "Files can be at most 25 MB.")]
    [InlineData(Megabyte, "Files can be at most 1 MB.")]
    [InlineData(300 * 1024, "Files can be at most 300 KB.")]
    public void A_file_over_the_upload_limit_is_told_the_limit_from_the_options(long maxParcelBytes, string detail)
    {
        var tooLarge = ParcelQuota.TooLarge(new ParcelOptions { MaxParcelBytes = maxParcelBytes });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooLarge.StatusCode);
        Assert.Equal("File too large", tooLarge.Title);
        Assert.Equal(detail, tooLarge.Detail);
    }
}
