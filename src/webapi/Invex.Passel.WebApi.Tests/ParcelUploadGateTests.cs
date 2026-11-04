using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelUploadGateTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uploads_beyond_the_limit_wait_and_are_refused_until_a_place_is_released()
    {
        using var gate = new ParcelUploadGate(Microsoft.Extensions.Options.Options.Create(new ParcelOptions
        {
            MaxConcurrentUploads = 2,
        }));

        Assert.True(await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken));
        Assert.True(await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken));
        Assert.False(await gate.TryEnterAsync(TimeSpan.FromMilliseconds(50), CancellationToken));

        gate.Release();

        Assert.True(await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken));
    }
}
