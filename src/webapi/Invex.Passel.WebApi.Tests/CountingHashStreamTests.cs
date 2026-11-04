using System.Security.Cryptography;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class CountingHashStreamTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static byte[] Bytes(int count) =>
        Enumerable.Range(0, count).Select(i => (byte)(i * 7)).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100_000)]
    public async Task A_body_of_exactly_the_limit_passes_with_its_count_and_hash(int length)
    {
        var bytes = Bytes(length);
        await using var counting = new CountingHashStream(new MemoryStream(bytes), maxBytes: length);
        using var copy = new MemoryStream();

        await counting.CopyToAsync(copy, CancellationToken);

        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal(length, counting.BytesRead);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), counting.GetSha256());
    }

    [Fact]
    public async Task A_body_one_byte_over_the_limit_throws()
    {
        await using var counting = new CountingHashStream(new MemoryStream(Bytes(100_001)), maxBytes: 100_000);

        await Assert.ThrowsAsync<ParcelTooLargeException>(() =>
            counting.CopyToAsync(Stream.Null, CancellationToken));
    }

    /// <summary>
    ///     A body sent without a length is read in whatever pieces arrive; the count holds across them, and the read
    ///     that crosses the limit throws.
    /// </summary>
    [Fact]
    public void Reads_in_small_pieces_are_counted_together()
    {
        using var counting = new CountingHashStream(new MemoryStream(Bytes(1000)), maxBytes: 999);
        var buffer = new byte[10];

        for (var read = 0; read < 99; read++)
            Assert.Equal(10, counting.Read(buffer, 0, buffer.Length));

        Assert.Equal(990, counting.BytesRead);
        Assert.Throws<ParcelTooLargeException>(() => counting.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task The_hash_of_a_body_read_in_pieces_is_the_hash_of_the_whole()
    {
        var bytes = Bytes(10_000);
        await using var counting = new CountingHashStream(new MemoryStream(bytes), maxBytes: bytes.Length);
        var buffer = new byte[333];

        while (await counting.ReadAsync(buffer, CancellationToken) > 0)
        {
        }

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), counting.GetSha256());
    }
}
