using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     Images made in the test, so no binary fixture has to be committed. SkiaSharp encodes PNG, JPEG and WebP; it
///     cannot write GIF or EXIF, so those bytes are written by hand.
/// </summary>
internal static class TestImages
{
    /// <summary>
    ///     The colours of <see cref="Quadrants" />: the top-left quarter white, the top-right red, the bottom half
    ///     black. Turning or mirroring the image moves the white and red quarters to places that tell the eight EXIF
    ///     orientations apart.
    /// </summary>
    public static readonly SKColor TopLeft = SKColors.White;

    public static readonly SKColor TopRight = SKColors.Red;

    public static SKBitmap Quadrants(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Black);

        using (var paint = new SKPaint { Color = TopLeft })
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);

        using (var paint = new SKPaint { Color = TopRight })
            canvas.DrawRect(width / 2f, 0, width / 2f, height / 2f, paint);

        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 95)
    {
        using var data = bitmap.Encode(format, quality) ??
                         throw new InvalidOperationException($"SkiaSharp cannot encode {format}.");

        return data.ToArray();
    }

    public static byte[] Png(int width, int height)
    {
        using var bitmap = Quadrants(width, height);

        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    public static byte[] Jpeg(int width, int height)
    {
        using var bitmap = Quadrants(width, height);

        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    public static byte[] Webp(int width, int height)
    {
        using var bitmap = Quadrants(width, height);

        return Encode(bitmap, SKEncodedImageFormat.Webp);
    }

    /// <summary>
    ///     A JPEG with an EXIF segment spliced in after its start marker: IFD0 with the orientation and, with
    ///     <paramref name="withGps" />, a pointer to a GPS IFD holding a latitude reference, the kind of metadata a
    ///     thumbnail must not carry.
    /// </summary>
    public static byte[] JpegWithExif(int width, int height, ushort orientation, bool withGps = true)
    {
        var tiff = new List<byte>();

        void U16(int value) =>
            tiff.AddRange([(byte)(value >> 8), (byte)value]);

        void U32(int value)
        {
            U16(value >> 16);
            U16(value & 0xFFFF);
        }

        // Big-endian TIFF header, IFD0 at offset 8
        tiff.AddRange("MM"u8.ToArray());
        U16(42);
        U32(8);

        var entries = withGps ? 2 : 1;
        U16(entries);

        // Orientation, SHORT, one value
        U16(0x0112);
        U16(3);
        U32(1);
        U16(orientation);
        U16(0);

        if (withGps)
        {
            // GPSInfo, LONG, the offset of the GPS IFD right after IFD0
            U16(0x8825);
            U16(4);
            U32(1);
            U32(8 + 2 + entries * 12 + 4);
        }

        // No next IFD
        U32(0);

        if (withGps)
        {
            // GPSLatitudeRef, ASCII, "N"
            U16(1);
            U16(0x0001);
            U16(2);
            U32(2);
            tiff.AddRange([(byte)'N', 0, 0, 0]);
            U32(0);
        }

        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, ..payload];

        var jpeg = Jpeg(width, height);

        return [..jpeg[..2], ..segment, ..jpeg[2..]];
    }

    /// <summary>
    ///     A GIF89a of <paramref name="frames" /> frames, 4 by 3, looping: the first red, the next blue, and so on. The
    ///     image data is LZW in name only: a clear code before every pixel keeps every code three bits wide.
    /// </summary>
    public static byte[] AnimatedGif(int frames = 2)
    {
        const int width = 4, height = 3;
        var gif = new List<byte>();

        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([width, 0, height, 0]);

        // A global palette of four colours, no background, square pixels
        gif.AddRange([0xF1, 0, 0]);
        gif.AddRange([255, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0]);

        // Loop for ever
        gif.AddRange([0x21, 0xFF, 11]);
        gif.AddRange("NETSCAPE2.0"u8.ToArray());
        gif.AddRange([3, 1, 0, 0, 0]);

        for (var frame = 0; frame < frames; frame++)
        {
            // Graphic control: a tenth of a second
            gif.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]);

            // The whole canvas, no local palette, minimum code size 2
            gif.AddRange([0x2C, 0, 0, 0, 0, width, 0, height, 0, 0]);
            gif.Add(2);

            var bits = new List<bool>();

            void Code(int code)
            {
                for (var bit = 0; bit < 3; bit++)
                    bits.Add(((code >> bit) & 1) == 1);
            }

            for (var pixel = 0; pixel < width * height; pixel++)
            {
                Code(4);
                Code(frame % 2);
            }

            Code(5);

            var data = new List<byte>();

            for (var i = 0; i < bits.Count; i += 8)
            {
                var value = 0;

                for (var j = 0; j < 8 && i + j < bits.Count; j++)
                    if (bits[i + j])
                        value |= 1 << j;

                data.Add((byte)value);
            }

            foreach (var block in data.Chunk(255))
            {
                gif.Add((byte)block.Length);
                gif.AddRange(block);
            }

            gif.Add(0);
        }

        gif.Add(0x3B);

        return gif.ToArray();
    }

    /// <summary>
    ///     A PNG that claims to be <paramref name="width" /> by <paramref name="height" /> and holds almost nothing: a
    ///     header with a valid checksum, a five-byte compressed stream and the end chunk. Decoding it would allocate
    ///     four bytes for every pixel claimed. Without the data chunk the codec would refuse it as incomplete, and the
    ///     pixel limit would never be reached.
    /// </summary>
    public static byte[] PngClaiming(int width, int height)
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);

        // Eight bits a channel, RGBA, deflate, adaptive filtering, not interlaced
        header[8] = 8;
        header[9] = 6;

        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00]);
        Chunk(png, "IEND", []);

        return png.ToArray();
    }

    /// <summary>
    ///     The ids of the chunks of a RIFF file (a WebP), in order.
    /// </summary>
    public static IReadOnlyList<string> RiffChunks(byte[] riff)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(riff, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(riff, 8, 4));

        var chunks = new List<string>();

        for (var at = 12; at + 8 <= riff.Length;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(at + 4));
            chunks.Add(Encoding.ASCII.GetString(riff, at, 4));
            at += 8 + length + (length & 1);
        }

        return chunks;
    }

    private static void Chunk(List<byte> png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.AddRange(length);

        byte[] typed = [..Encoding.ASCII.GetBytes(type), ..data];
        png.AddRange(typed);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.AddRange(crc);
    }

    /// <summary>
    ///     The CRC-32 a PNG chunk carries (ISO 3309, reflected, polynomial 0xEDB88320), over its type and data.
    /// </summary>
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in bytes)
        {
            crc ^= b;

            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
        }

        return ~crc;
    }
}
