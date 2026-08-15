using System.Buffers.Binary;
using System.IO.Compression;

namespace GBSharp.Assets.Images;

/// <summary>
/// Writes minimal PNGs in memory.
/// </summary>
/// <remarks>
/// <para>
/// GB# is a decoder first, but writing PNGs earns its place twice. It lets
/// <c>gbsharp new</c> synthesise the placeholder art a template needs, so no
/// template has to ship a binary; and it lets the decoder be tested against an
/// independent encoder, which catches filter and stride mistakes that a single
/// hand-made fixture never would.
/// </para>
/// <para>
/// Deliberately plain: one filter, one compression path, no interlacing. This
/// exists to produce files GB# and every image editor can read, not to compete
/// with a real encoder.
/// </para>
/// </remarks>
public static class PngEncoder
{
    /// <summary>An 8-bit truecolour PNG.</summary>
    public static byte[] Rgb(int width, int height, Func<int, int, Rgba32> pixel) =>
        Write(width, height, colorType: 2, bitDepth: 8, palette: null, row =>
        {
            var bytes = new byte[width * 3];

            for (int x = 0; x < width; x++)
            {
                Rgba32 color = pixel(x, row);
                bytes[x * 3] = color.R;
                bytes[(x * 3) + 1] = color.G;
                bytes[(x * 3) + 2] = color.B;
            }

            return bytes;
        });

    /// <summary>
    /// An 8-bit indexed PNG, keeping the palette order given.
    /// </summary>
    /// <remarks>
    /// The order matters downstream: GB# reads PLTE to let an artist decide which
    /// colour becomes which shade rather than inferring it from brightness.
    /// </remarks>
    public static byte[] Indexed(int width, int height, Rgba32[] palette, Func<int, int, byte> index) =>
        Write(width, height, colorType: 3, bitDepth: 8, palette, row =>
        {
            var bytes = new byte[width];

            for (int x = 0; x < width; x++)
            {
                bytes[x] = index(x, row);
            }

            return bytes;
        });

    /// <summary>A greyscale PNG at 1, 2, 4 or 8 bits per pixel.</summary>
    public static byte[] Grey(int width, int height, int bitDepth, Func<int, int, byte> value)
    {
        int max = (1 << bitDepth) - 1;

        return Write(width, height, colorType: 0, bitDepth, palette: null, row =>
        {
            var bytes = new byte[((width * bitDepth) + 7) / 8];

            for (int x = 0; x < width; x++)
            {
                int bit = x * bitDepth;
                bytes[bit / 8] |= (byte)((value(x, row) & max) << (8 - bitDepth - (bit % 8)));
            }

            return bytes;
        });
    }

    /// <summary>
    /// A PNG claiming to be interlaced, which GB# must refuse.
    /// </summary>
    /// <remarks>
    /// Only useful for testing that refusal. Nothing produces a usable image.
    /// </remarks>
    public static byte[] Interlaced(int width, int height) =>
        Write(width, height, colorType: 2, bitDepth: 8, palette: null, _ => new byte[width * 3], interlace: 1);

    private static byte[] Write(
        int width,
        int height,
        int colorType,
        int bitDepth,
        Rgba32[]? palette,
        Func<int, byte[]> scanline,
        byte interlace = 0)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = interlace;
        Chunk(output, "IHDR", header);

        if (palette is not null)
        {
            var plte = new byte[palette.Length * 3];

            for (int i = 0; i < palette.Length; i++)
            {
                plte[i * 3] = palette[i].R;
                plte[(i * 3) + 1] = palette[i].G;
                plte[(i * 3) + 2] = palette[i].B;
            }

            Chunk(output, "PLTE", plte);
        }

        using var raw = new MemoryStream();

        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // filter: none
            raw.Write(scanline(y));
        }

        using var compressed = new MemoryStream();

        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        stream.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(typed));
        stream.Write(crc);
    }
}
