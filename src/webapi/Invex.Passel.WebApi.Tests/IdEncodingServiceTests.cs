using Invex.Passel.WebApi.Services;
using Microsoft.Extensions.Configuration;

namespace Invex.Passel.WebApi.Tests;

public sealed class IdEncodingServiceTests
{
    private readonly IdEncodingService _service = new(new ConfigurationBuilder().Build());

    [Fact]
    public void Encode_then_TryDecode_round_trips()
    {
        var id = Guid.CreateVersion7();

        var encoded = _service.Encode(id);

        Assert.True(_service.TryDecode(encoded, out var decoded));
        Assert.Equal(id, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-cursor!")]
    [InlineData("abc")]
    public void TryDecode_rejects_invalid_input(string? input)
    {
        Assert.False(_service.TryDecode(input, out var decoded));
        Assert.Equal(Guid.Empty, decoded);
    }

    [Fact]
    public void TryDecode_rejects_non_canonical_encoding()
    {
        var encoded = _service.Encode(Guid.CreateVersion7());

        // Appending characters can still decode to numbers, but never to this canonical form
        Assert.False(_service.TryDecode(encoded + "a", out _));
    }
}
