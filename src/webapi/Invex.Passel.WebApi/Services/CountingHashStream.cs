using System.Security.Cryptography;

namespace Invex.Passel.WebApi.Services;

/// <summary>
///     A file longer than the upload limit, found while reading it. Answered with 413.
/// </summary>
internal sealed class ParcelTooLargeException(long maxBytes)
    : Exception($"The file is longer than the {maxBytes} bytes an upload may carry.");

/// <summary>
///     Reads an upload's body through to the caller while counting its bytes and hashing them with SHA-256, so the
///     file is measured and hashed in the one pass that buffers it. Reading past <paramref name="maxBytes" /> throws
///     a <see cref="ParcelTooLargeException" />: this, not the server's request size limit, is what holds a body
///     sent without a length, and under the test server, which applies no limit.
/// </summary>
internal sealed class CountingHashStream(Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>
    ///     How many bytes have been read so far.
    /// </summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length =>
        throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     The SHA-256 of the bytes read, in lower-case hex. Call it once, after the last read.
    /// </summary>
    public string GetSha256() =>
        Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) =>
        Count(buffer[..inner.Read(buffer)]);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);

        return Count(buffer.Span[..read]);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();

        base.Dispose(disposing);
    }

    private int Count(ReadOnlySpan<byte> read)
    {
        BytesRead += read.Length;

        if (BytesRead > maxBytes)
            throw new ParcelTooLargeException(maxBytes);

        _hash.AppendData(read);

        return read.Length;
    }
}
