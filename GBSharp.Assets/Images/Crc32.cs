namespace GBSharp.Assets.Images;

/// <summary>
/// CRC-32, as PNG defines it.
/// </summary>
/// <remarks>
/// Hand-rolled because <c>System.IO.Hashing</c> is a package, and this project
/// takes no package dependencies. It is fifteen lines and the polynomial has
/// not changed since 1996.
/// </remarks>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFU;

        foreach (byte b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFU;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
