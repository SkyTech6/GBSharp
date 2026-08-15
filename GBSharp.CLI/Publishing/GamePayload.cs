using System.Buffers.Binary;

namespace GBSharp.Cli.Publishing;

/// <summary>
/// Writes a game into a copy of the Player.
/// </summary>
/// <remarks>
/// <para>
/// A published game is the prebuilt Player with the ROM, the window settings and
/// a trailer appended to it:
/// </para>
/// <code>
/// MyGame.exe = gbsharp-player.exe + [ROM] + [config JSON] + [trailer]
/// </code>
/// <para>
/// The Player reads its own file at startup, finds the trailer in the last bytes,
/// and loads the ROM from memory. Nothing is unpacked to disk and nothing is
/// relinked, which is what lets publishing work on a machine with no C toolchain
/// and work the same way on every platform: PE, ELF and Mach-O loaders all
/// ignore bytes past the end of the image they describe.
/// </para>
/// <para>
/// The layout here is the other half of <c>player/payload.h</c> in the emulator
/// repository. The two are versioned together through
/// <see cref="TrailerVersion"/>: a Player refuses a payload whose version it does
/// not know rather than reading offsets out of a format it is guessing at.
/// </para>
/// </remarks>
public static class GamePayload
{
    /// <summary>"GB#P" little endian, written at both ends of the trailer.</summary>
    public const uint Magic = 0x50234247;

    /// <summary>Bumped whenever the trailer layout changes.</summary>
    public const uint TrailerVersion = 1;

    /// <summary>Bytes of trailer, fixed so the Player can seek to it from the end.</summary>
    public const int TrailerSize = 48;

    /// <summary>
    /// Copies <paramref name="stubPath"/> to <paramref name="outputPath"/> with
    /// the game appended.
    /// </summary>
    public static void Write(string stubPath, string outputPath, byte[] rom, string config)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rom.Length);

        byte[] configBytes = System.Text.Encoding.UTF8.GetBytes(config);
        long stubLength = new FileInfo(stubPath).Length;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Copied rather than opened for append, so a failure part way through
        // leaves the stub intact and the half-written game obviously incomplete.
        File.Copy(stubPath, outputPath, overwrite: true);

        using FileStream output = File.Open(outputPath, FileMode.Open, FileAccess.Write);
        output.Seek(0, SeekOrigin.End);

        long romOffset = stubLength;
        long configOffset = romOffset + rom.Length;

        output.Write(rom);
        output.Write(configBytes);
        output.Write(BuildTrailer(romOffset, rom.Length, configOffset, configBytes.Length,
                                  unchecked(Adler32(rom) + Adler32(configBytes))));
    }

    private static byte[] BuildTrailer(
        long romOffset, long romSize, long configOffset, long configSize, uint checksum)
    {
        byte[] trailer = new byte[TrailerSize];
        Span<byte> span = trailer;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], TrailerVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)romOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)romSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)configOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], (ulong)configSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], checksum);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], Magic);

        return trailer;
    }

    /// <summary>
    /// Adler-32, which is what the Player checks the payload against.
    /// </summary>
    /// <remarks>
    /// Enough to catch a truncated download or a corrupted copy, which is all it
    /// is for. A payload altered deliberately is a code signing question, and no
    /// checksum stored beside the data it describes could answer it.
    /// </remarks>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
