using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Invex.Passel.WebApi.Options;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelOptionsTests
{
    /// <summary>
    ///     What <c>ValidateDataAnnotations</c> reports for <paramref name="options" />: the ranges first, then, when
    ///     they all hold, the cross-checks of <see cref="ParcelOptions.Validate" />.
    /// </summary>
    private static List<ValidationResult> Validate(ParcelOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new(options), results, validateAllProperties: true);

        return results;
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Empty(Validate(new()));
    }

    [Fact]
    public void The_defaults_are_the_documented_limits()
    {
        var options = new ParcelOptions();

        Assert.Equal("parcels", options.ContainerName);
        Assert.Equal(25L * 1024 * 1024, options.MaxParcelBytes);
        Assert.Equal(TimeSpan.FromHours(1), options.UnattachedLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), options.UploadingGrace);
        Assert.Equal(512, options.ThumbnailSize);
        Assert.Equal(TimeSpan.FromSeconds(5), options.DecodeSlotTimeout);
    }

    [Theory]
    [InlineData("00:15:00", "00:15:00")]
    [InlineData("00:30:00", "00:15:00")]
    public void An_uploading_grace_as_long_as_the_unattached_lifetime_is_refused(string grace, string lifetime)
    {
        var results = Validate(new()
        {
            UploadingGrace = TimeSpan.Parse(grace),
            UnattachedLifetime = TimeSpan.Parse(lifetime),
        });

        var result = Assert.Single(results);
        Assert.Contains(nameof(ParcelOptions.UploadingGrace), result.MemberNames);
    }

    [Fact]
    public void The_store_is_chosen_by_the_connection_string_unless_it_is_named()
    {
        var options = new ParcelOptions();

        Assert.Equal(ParcelStoreKind.Auto, options.Store);
        Assert.Null(options.FileSystemRoot);
    }

    [Fact]
    public void The_filesystem_store_without_a_root_is_refused()
    {
        var result = Assert.Single(Validate(new() { Store = ParcelStoreKind.FileSystem }));

        Assert.Contains(nameof(ParcelOptions.FileSystemRoot), result.MemberNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("parcels")]
    [InlineData("./parcels")]
    public void The_filesystem_store_with_a_root_that_is_not_absolute_is_refused(string root)
    {
        var result = Assert.Single(Validate(new() { Store = ParcelStoreKind.FileSystem, FileSystemRoot = root }));

        Assert.Contains(nameof(ParcelOptions.FileSystemRoot), result.MemberNames);
    }

    [Fact]
    public void The_filesystem_store_with_an_absolute_root_is_valid()
    {
        Assert.Empty(Validate(new()
        {
            Store = ParcelStoreKind.FileSystem,
            FileSystemRoot = Path.Combine(Path.GetTempPath(), "x"),
        }));
    }

    [Fact]
    public void The_azure_store_named_without_its_connection_string_is_refused()
    {
        var result = Assert.Single(Validate(new() { Store = ParcelStoreKind.AzureBlob }));

        Assert.Contains(nameof(ParcelOptions.Store), result.MemberNames);

        Assert.Empty(Validate(new() { Store = ParcelStoreKind.AzureBlob, BlobConnectionConfigured = true }));
    }

    [Fact]
    public void A_missing_container_name_is_refused()
    {
        var result = Assert.Single(Validate(new() { ContainerName = "" }));

        Assert.Contains(nameof(ParcelOptions.ContainerName), result.MemberNames);
    }

    /// <summary>
    ///     Each value is set as configuration binding would set it, from its invariant string form.
    /// </summary>
    [Theory]
    [InlineData(nameof(ParcelOptions.MaxParcelBytes), "0")]
    [InlineData(nameof(ParcelOptions.MaxParcelBytes), "1073741825")]
    [InlineData(nameof(ParcelOptions.UnattachedLifetime), "8.00:00:00")]
    [InlineData(nameof(ParcelOptions.UploadingGrace), "00:00:00")]
    [InlineData(nameof(ParcelOptions.SweepInterval), "02:00:00")]
    [InlineData(nameof(ParcelOptions.OrphanScanInterval), "00:00:30")]
    [InlineData(nameof(ParcelOptions.OrphanBlobMinimumAge), "8.00:00:00")]
    [InlineData(nameof(ParcelOptions.SweepBatchSize), "0")]
    [InlineData(nameof(ParcelOptions.ThumbnailSize), "63")]
    [InlineData(nameof(ParcelOptions.ThumbnailSize), "2049")]
    [InlineData(nameof(ParcelOptions.MaxImagePixels), "268435457")]
    [InlineData(nameof(ParcelOptions.MaxImageDimension), "0")]
    [InlineData(nameof(ParcelOptions.DecodeSlotTimeout), "00:01:01")]
    [InlineData(nameof(ParcelOptions.DecodeSlotTimeout), "-00:00:01")]
    [InlineData(nameof(ParcelOptions.MaxConcurrentUploads), "65")]
    public void A_value_out_of_its_range_is_refused(string member, string value)
    {
        var options = new ParcelOptions();
        var property = typeof(ParcelOptions).GetProperty(member)!;
        property.SetValue(options, TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(value));

        var result = Assert.Single(Validate(options));

        Assert.Contains(member, result.MemberNames);
    }
}
