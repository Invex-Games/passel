using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelKeysTests
{
    private static readonly Guid Id = Guid.Parse("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0b01");

    [Fact]
    public void The_names_carry_the_id_alone()
    {
        Assert.Equal("0199fd3e5a107c4e9b2a3f6d8e1c0b01", ParcelKeys.Original(Id));
        Assert.Equal("0199fd3e5a107c4e9b2a3f6d8e1c0b01.thumb", ParcelKeys.Thumbnail(Id));
    }

    [Fact]
    public void Both_names_parse_back_to_the_parcel()
    {
        Assert.True(ParcelKeys.TryParse(ParcelKeys.Original(Id), out var original));
        Assert.True(ParcelKeys.TryParse(ParcelKeys.Thumbnail(Id), out var thumbnail));

        Assert.Equal(Id, original);
        Assert.Equal(Id, thumbnail);
    }

    /// <summary>
    ///     The orphan scan deletes only blobs whose name parses, so anything else in the container is left alone.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(".thumb")]
    [InlineData("0199fd3e-5a10-7c4e-9b2a-3f6d8e1c0b01")]
    [InlineData("0199FD3E5A107C4E9B2A3F6D8E1C0B01")]
    [InlineData("0199FD3E5A107C4E9B2A3F6D8E1C0B01.thumb")]
    [InlineData("0199fd3e5a107c4e9b2a3f6d8e1c0B01")]
    [InlineData("0199fd3e5a107c4e9b2a3f6d8e1c0b01.png")]
    [InlineData("0199fd3e5a107c4e9b2a3f6d8e1c0b01.thumb.thumb")]
    [InlineData("users/0199fd3e5a107c4e9b2a3f6d8e1c0b01")]
    public void Any_other_name_does_not_parse(string name)
    {
        Assert.False(ParcelKeys.TryParse(name, out _));
    }
}
