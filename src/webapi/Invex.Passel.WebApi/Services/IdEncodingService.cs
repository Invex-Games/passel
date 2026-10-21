namespace Invex.Passel.WebApi.Services;

public interface IIdEncodingService
{
    string Encode(Guid id);

    bool TryDecode(string? hash, out Guid id);
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

    public bool TryDecode(string? hash, out Guid id)
    {
        id = Guid.Empty;

        if (string.IsNullOrEmpty(hash))
            return false;

        var numbers = _encoder.Decode(hash);

        if (numbers.Count != 2)
            return false;

        // Sqids decodes several strings to the same numbers; only accept the canonical encoding
        if (_encoder.Encode(numbers[0], numbers[1]) != hash)
            return false;

        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..8], numbers[0]);
        BitConverter.TryWriteBytes(bytes[8..], numbers[1]);

        id = new(bytes);

        return true;
    }
}
