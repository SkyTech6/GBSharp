using System.Buffers.Binary;
using System.IO.Compression;

namespace GBSharp.Assets.Images;

/// <summary>A decoded image.</summary>
/// <param name="SourcePalette">
/// The PLTE entries, for indexed images. Present so an artist can control which
/// colour becomes which shade by ordering their own palette.
/// </param>
public sealed record DecodedImage(
    int Width,
    int Height,
    Rgba32[] Pixels,
    Rgba32[]? SourcePalette)
{
    public Rgba32 this[int x, int y] => Pixels[(y * Width) + x];
}

/// <summary>Why an image could not be read.</summary>
/// <param name="IsUnsupportedFeature">
/// True when the file is a valid PNG using something GB# does not read, which
/// is a different message from a file that is simply broken.
/// </param>
public sealed record PngFailure(string Message, bool IsUnsupportedFeature);

/// <summary>
/// Reads the PNGs a Game Boy artist actually produces.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-rolled and deliberately narrow. The alternative was a
/// package, and this project pins none: an image library would decide the
/// repository's licence question for it, and would turn every rejection into
/// someone else's exception message rather than a GB# diagnostic that names
/// the problem and the fix.
/// </para>
/// <para>
/// Supported: colour types 0, 2, 3, 4 and 6, bit depths 1 to 8, all five
/// filters, and <c>tRNS</c>. Not supported, and reported as such: interlacing,
/// 16-bit channels, and APNG.
/// </para>
/// <para>
/// No exception escapes this class. A truncated or malformed file is a
/// diagnostic, never a crash, because the input is a file someone is editing
/// while the build watches it.
/// </para>
/// </remarks>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryDecode(byte[] bytes, out DecodedImage? image, out PngFailure? failure)
    {
        image = null;

        try
        {
            return TryDecodeCore(bytes, out image, out failure);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException
                                       or OverflowException or InvalidDataException or ArgumentException)
        {
            // The decoder is defensive throughout, but a file being written
            // while it is read can produce shapes no amount of checking covers.
            failure = new PngFailure("the file is truncated or corrupt", IsUnsupportedFeature: false);
            return false;
        }
    }

    private static bool TryDecodeCore(byte[] bytes, out DecodedImage? image, out PngFailure? failure)
    {
        image = null;

        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
        {
            failure = new PngFailure("it does not start with a PNG signature", IsUnsupportedFeature: false);
            return false;
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        bool seenHeader = false;
        bool seenEnd = false;
        Rgba32[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        int offset = 8;

        while (offset + 8 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > bytes.Length)
            {
                failure = new PngFailure("a chunk runs past the end of the file", IsUnsupportedFeature: false);
                return false;
            }

            string type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            ReadOnlySpan<byte> data = bytes.AsSpan(offset + 8, length);

            uint declared = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));
            if (Crc32.Compute(bytes.AsSpan(offset + 4, 4 + length)) != declared)
            {
                failure = new PngFailure($"the {type} chunk is corrupt", IsUnsupportedFeature: false);
                return false;
            }

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        failure = new PngFailure("the header chunk is too short", IsUnsupportedFeature: false);
                        return false;
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    seenHeader = true;

                    if (width <= 0 || height <= 0 || (long)width * height > 4_000_000)
                    {
                        failure = new PngFailure($"its dimensions are {width}x{height}", IsUnsupportedFeature: false);
                        return false;
                    }

                    if (data[12] != 0)
                    {
                        failure = new PngFailure("interlacing", IsUnsupportedFeature: true);
                        return false;
                    }

                    if (bitDepth == 16)
                    {
                        failure = new PngFailure("16 bits per channel", IsUnsupportedFeature: true);
                        return false;
                    }

                    if (bitDepth is not (1 or 2 or 4 or 8) || colorType is not (0 or 2 or 3 or 4 or 6))
                    {
                        failure = new PngFailure(
                            $"colour type {colorType} at {bitDepth} bits per channel",
                            IsUnsupportedFeature: true);
                        return false;
                    }

                    break;

                case "PLTE":
                    palette = new Rgba32[length / 3];
                    for (int i = 0; i < palette.Length; i++)
                    {
                        palette[i] = new Rgba32(data[i * 3], data[(i * 3) + 1], data[(i * 3) + 2], 255);
                    }

                    break;

                case "tRNS":
                    transparency = data.ToArray();
                    break;

                case "acTL":
                    failure = new PngFailure("animation (APNG)", IsUnsupportedFeature: true);
                    return false;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    seenEnd = true;
                    offset = bytes.Length;
                    break;

                default:
                    // An unknown chunk whose first letter is uppercase is
                    // critical: the spec says a decoder that does not know it
                    // must not proceed.
                    if (char.IsUpper(type[0]))
                    {
                        failure = new PngFailure($"the unsupported '{type}' chunk", IsUnsupportedFeature: true);
                        return false;
                    }

                    break;
            }

            if (offset >= bytes.Length)
            {
                break;
            }

            offset += 12 + length;
        }

        if (!seenHeader || idat.Length == 0)
        {
            failure = new PngFailure("it has no image data", IsUnsupportedFeature: false);
            return false;
        }

        // A file that stops before IEND is truncated. The image data it does
        // carry may well inflate cleanly, so without this check a half-written
        // file decodes into a half-drawn image rather than an error.
        if (!seenEnd)
        {
            failure = new PngFailure("it ends before the final chunk", IsUnsupportedFeature: false);
            return false;
        }

        if (colorType == 3 && palette is null)
        {
            failure = new PngFailure("it is indexed but has no palette", IsUnsupportedFeature: false);
            return false;
        }

        ApplyTransparency(palette, transparency, colorType);

        byte[] raw = Inflate(idat);
        int channels = ChannelsFor(colorType);
        int bitsPerPixel = channels * bitDepth;
        int stride = ((width * bitsPerPixel) + 7) / 8;

        if (raw.Length < (long)(stride + 1) * height)
        {
            failure = new PngFailure("the compressed data is shorter than the image", IsUnsupportedFeature: false);
            return false;
        }

        byte[] unfiltered = Unfilter(raw, width, height, stride, Math.Max(1, bitsPerPixel / 8));
        Rgba32[] pixels = Expand(unfiltered, width, height, stride, bitDepth, colorType, palette);

        image = new DecodedImage(width, height, pixels, palette);
        failure = null;
        return true;
    }

    private static void ApplyTransparency(Rgba32[]? palette, byte[]? transparency, int colorType)
    {
        if (palette is null || transparency is null || colorType != 3)
        {
            return;
        }

        for (int i = 0; i < transparency.Length && i < palette.Length; i++)
        {
            palette[i] = palette[i] with { A = transparency[i] };
        }
    }

    private static byte[] Inflate(MemoryStream idat)
    {
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int ChannelsFor(int colorType) => colorType switch
    {
        0 => 1,   // greyscale
        2 => 3,   // truecolour
        3 => 1,   // indexed
        4 => 2,   // greyscale + alpha
        _ => 4,   // truecolour + alpha
    };

    /// <summary>
    /// Reverses PNG's per-scanline filters.
    /// </summary>
    /// <remarks>
    /// Each row names one of five predictors and stores the difference from it.
    /// <paramref name="bytesPerPixel"/> is the filter's notion of "the pixel to
    /// the left", which is a whole byte count and therefore 1 for anything
    /// narrower than 8 bits per channel.
    /// </remarks>
    private static byte[] Unfilter(byte[] raw, int width, int height, int stride, int bytesPerPixel)
    {
        var output = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int source = (y * (stride + 1)) + 1;
            int target = y * stride;
            int above = target - stride;

            for (int x = 0; x < stride; x++)
            {
                int value = raw[source + x];
                int left = x >= bytesPerPixel ? output[target + x - bytesPerPixel] : 0;
                int up = y > 0 ? output[above + x] : 0;
                int upLeft = y > 0 && x >= bytesPerPixel ? output[above + x - bytesPerPixel] : 0;

                output[target + x] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) / 2),
                    4 => value + Paeth(left, up, upLeft),
                    _ => value,
                });
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static Rgba32[] Expand(
        byte[] data,
        int width,
        int height,
        int stride,
        int bitDepth,
        int colorType,
        Rgba32[]? palette)
    {
        var pixels = new Rgba32[width * height];
        int channels = ChannelsFor(colorType);
        int max = (1 << bitDepth) - 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;

                if (bitDepth == 8)
                {
                    int at = row + (x * channels);

                    pixels[index] = colorType switch
                    {
                        0 => Grey(data[at], 255),
                        2 => new Rgba32(data[at], data[at + 1], data[at + 2], 255),
                        3 => Indexed(palette, data[at]),
                        4 => Grey(data[at], data[at + 1]),
                        _ => new Rgba32(data[at], data[at + 1], data[at + 2], data[at + 3]),
                    };

                    continue;
                }

                // Sub-byte depths pack several pixels per byte, most
                // significant bits first, and only occur for greyscale and
                // indexed images.
                int bit = x * bitDepth;
                int value = (data[row + (bit / 8)] >> (8 - bitDepth - (bit % 8))) & max;

                pixels[index] = colorType == 3
                    ? Indexed(palette, value)
                    : Grey((byte)(value * 255 / max), 255);
            }
        }

        return pixels;

        static Rgba32 Grey(byte v, byte a) => new(v, v, v, a);

        static Rgba32 Indexed(Rgba32[]? palette, int i) =>
            palette is not null && i < palette.Length ? palette[i] : new Rgba32(0, 0, 0, 255);
    }
}
