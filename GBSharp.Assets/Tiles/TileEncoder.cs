namespace GBSharp.Assets.Tiles;

/// <summary>One 8x8 tile in the hardware's own format.</summary>
/// <remarks>
/// Sixteen bytes, two per row. The first byte of a row holds the low bit of all
/// eight pixels and the second holds the high bit, so a pixel's colour index is
/// split across two bytes eight bits apart. This is the format VRAM wants, and
/// converting to it is most of what an asset pipeline does.
/// </remarks>
public readonly struct GbTile : IEquatable<GbTile>
{
    public const int Bytes = 16;
    public const int Size = 8;

    private readonly byte[] _data;

    private GbTile(byte[] data) => _data = data;

    public ReadOnlySpan<byte> Data => _data;

    /// <summary>
    /// Encodes 64 colour indices, row-major, each 0-3.
    /// </summary>
    public static GbTile Encode(ReadOnlySpan<byte> indices)
    {
        var data = new byte[Bytes];

        for (int y = 0; y < Size; y++)
        {
            byte low = 0;
            byte high = 0;

            for (int x = 0; x < Size; x++)
            {
                int value = indices[(y * Size) + x] & 0x03;

                // Bit 7 is the leftmost pixel.
                int bit = 7 - x;
                low |= (byte)((value & 1) << bit);
                high |= (byte)(((value >> 1) & 1) << bit);
            }

            data[y * 2] = low;
            data[(y * 2) + 1] = high;
        }

        return new GbTile(data);
    }

    /// <summary>The colour indices this tile draws, row-major.</summary>
    public byte[] ToIndices()
    {
        var indices = new byte[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            byte low = _data[y * 2];
            byte high = _data[(y * 2) + 1];

            for (int x = 0; x < Size; x++)
            {
                int bit = 7 - x;
                indices[(y * Size) + x] = (byte)(((low >> bit) & 1) | (((high >> bit) & 1) << 1));
            }
        }

        return indices;
    }

    public GbTile FlippedX()
    {
        byte[] indices = ToIndices();
        var flipped = new byte[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                flipped[(y * Size) + x] = indices[(y * Size) + (Size - 1 - x)];
            }
        }

        return Encode(flipped);
    }

    public GbTile FlippedY()
    {
        var flipped = new byte[Bytes];

        for (int y = 0; y < Size; y++)
        {
            flipped[y * 2] = _data[(Size - 1 - y) * 2];
            flipped[(y * 2) + 1] = _data[((Size - 1 - y) * 2) + 1];
        }

        return new GbTile(flipped);
    }

    public bool Equals(GbTile other) => _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => obj is GbTile other && Equals(other);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }
}
