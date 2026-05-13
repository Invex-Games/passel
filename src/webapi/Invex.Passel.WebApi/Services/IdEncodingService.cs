namespace Invex.Passel.WebApi.Services;

public interface IIdEncodingService
{
    string Encode(Guid id);

    Guid Decode(string hash);
}

internal sealed class IdEncodingService : IIdEncodingService
{
    private readonly SqidsEncoder<ulong> _encoder;

    public IdEncodingService(IConfiguration config)
    {
        var alphabet = config["Encoding:Alphabet"] ?? "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        _encoder = new(new()
        {
            Alphabet = alphabet,
            MinLength = 10,
        });
    }

    public string Encode(Guid id)
    {
        // Split the 16 bytes of the GUID into two 8-byte halves
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);

        var low = BitConverter.ToUInt64(bytes[..8]);
        var high = BitConverter.ToUInt64(bytes[8..]);

        // Sqids handles multiple numbers and produces a single string
        return _encoder.Encode(low, high);
    }

    public Guid Decode(string? hash)
    {
        if (hash is null)
            return Guid.Empty;

        var numbers = _encoder.Decode(hash);

        if (numbers.Count < 2)
            return Guid.Empty;

        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..8], numbers[0]);
        BitConverter.TryWriteBytes(bytes[8..], numbers[1]);

        return new(bytes);
    }
}
