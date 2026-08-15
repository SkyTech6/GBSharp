namespace GBSharp.Backend.GBDK;

/// <summary>
/// Reads and validates a Game Boy cartridge header.
/// </summary>
/// <remarks>
/// Cheap insurance, and the strongest correctness check available without an
/// emulator: a ROM whose logo and header checksum are right is a ROM the boot
/// ROM will actually run. Used to verify builds and as the integration tests'
/// definition of "this booted".
/// </remarks>
public sealed class RomHeader
{
    /// <summary>The Nintendo logo the boot ROM compares against before running anything.</summary>
    private static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    private const int LogoOffset = 0x104;
    private const int TitleOffset = 0x134;
    private const int CgbFlagOffset = 0x143;
    private const int HeaderChecksumStart = 0x134;
    private const int CartridgeTypeOffset = 0x147;
    private const int RomSizeOffset = 0x148;
    private const int RamSizeOffset = 0x149;
    private const int HeaderChecksumOffset = 0x14D;
    private const int GlobalChecksumOffset = 0x14E;

    /// <summary>The smallest legal ROM: two 16 KB banks with no mapper.</summary>
    public const int MinimumRomSize = 0x8000;

    /// <summary>The largest value byte 0x148 can meaningfully hold (8 MB).</summary>
    private const int MaxRomSizeCode = 0x08;

    private RomHeader(byte[] rom)
    {
        Title = ReadTitle(rom);
        IsColorEnabled = rom[CgbFlagOffset] is 0x80 or 0xC0;
        HasValidLogo = rom.AsSpan(LogoOffset, NintendoLogo.Length).SequenceEqual(NintendoLogo);

        CartridgeType = rom[CartridgeTypeOffset];
        RomSizeCode = rom[RomSizeOffset];
        RamSizeCode = rom[RamSizeOffset];

        StoredHeaderChecksum = rom[HeaderChecksumOffset];
        ComputedHeaderChecksum = ComputeHeaderChecksum(rom);

        StoredGlobalChecksum = (ushort)((rom[GlobalChecksumOffset] << 8) | rom[GlobalChecksumOffset + 1]);
        ComputedGlobalChecksum = ComputeGlobalChecksum(rom);

        SizeInBytes = rom.Length;
    }

    public string Title { get; }

    public bool IsColorEnabled { get; }

    public bool HasValidLogo { get; }

    public byte StoredHeaderChecksum { get; }

    public byte ComputedHeaderChecksum { get; }

    public ushort StoredGlobalChecksum { get; }

    public ushort ComputedGlobalChecksum { get; }

    public int SizeInBytes { get; }

    /// <summary>Byte 0x147: the mapper and what else is on the cartridge.</summary>
    public byte CartridgeType { get; }

    /// <summary>Byte 0x148: the ROM size, as a power-of-two code.</summary>
    public byte RomSizeCode { get; }

    /// <summary>Byte 0x149: how much save RAM is on the cartridge, as a code.</summary>
    public byte RamSizeCode { get; }

    /// <summary>True if the cartridge declares a memory bank controller.</summary>
    public bool HasMbc => CartridgeType != 0x00;

    /// <summary>
    /// The cartridge type bytes that say RAM is present.
    /// </summary>
    /// <remarks>
    /// The full hardware set rather than only the mappers GB# emits, because
    /// <see cref="Parse"/> is pointed at ROMs GB# did not build. A table that
    /// was right for MBC5 and wrong for MBC3 would be worse than no table.
    /// </remarks>
    private static readonly byte[] TypesWithRam =
    [
        0x02, 0x03, // MBC1+RAM
        0x08, 0x09, // ROM+RAM
        0x0C, 0x0D, // MMM01+RAM
        0x10, 0x12, 0x13, // MBC3+RAM
        0x1A, 0x1B, // MBC5+RAM
        0x1D, 0x1E, // MBC5+RUMBLE+RAM
        0x22, // MBC7, whose sensor EEPROM sits behind the same window
        0xFE, 0xFF, // HuC3, HuC1+RAM
    ];

    /// <summary>
    /// How many 16 KB banks the header says the cartridge has.
    /// </summary>
    /// <remarks>
    /// The encoding is <c>2 &lt;&lt; code</c>: 0x00 is two banks (32 KB), 0x01 is
    /// four, and so on. Clamped, because a nonsense code should give a wrong
    /// answer rather than an enormous shift.
    /// </remarks>
    public int DeclaredRomBanks => 2 << Math.Min((int)RomSizeCode, MaxRomSizeCode);

    /// <summary>The ROM size the header declares, in bytes.</summary>
    public int DeclaredRomSize => DeclaredRomBanks * 0x4000;

    /// <summary>
    /// True if the file is exactly the size its own header claims.
    /// </summary>
    /// <remarks>
    /// This is what catches a wrong bank count or an image truncated after
    /// linking. It holds trivially for an unbanked 32 KB ROM, so it costs
    /// nothing until banking is in play, which is when it starts earning.
    /// </remarks>
    public bool RomSizeMatchesHeader => SizeInBytes == DeclaredRomSize;

    /// <summary>
    /// The save RAM the header declares, in bytes.
    /// </summary>
    /// <remarks>
    /// Not a power-of-two code like the ROM size: the encoding is a short table
    /// with a gap at 0x01, which was 2 KB on hardware that was never produced.
    /// Anything outside the table is nothing, which is what an emulator does
    /// with it too.
    /// </remarks>
    public int DeclaredRamSize => RamSizeCode switch
    {
        0x02 => 8 * 1024,
        0x03 => 32 * 1024,
        0x04 => 128 * 1024,
        0x05 => 64 * 1024,
        _ => 0,
    };

    /// <summary>
    /// True if byte 0x147 and byte 0x149 tell the same story about save RAM.
    /// </summary>
    /// <remarks>
    /// A cartridge type naming RAM with none reserved is the interesting half:
    /// the header advertises a battery, an emulator offers to save, and there
    /// is nothing behind it. Worth a check because both halves are written by
    /// separate linker flags, so nothing else makes them agree.
    /// </remarks>
    public bool RamMatchesCartridgeType =>
        Array.IndexOf(TypesWithRam, CartridgeType) >= 0 == (DeclaredRamSize > 0);

    /// <summary>
    /// True if the boot ROM would accept this cartridge. The global checksum is
    /// included even though hardware ignores it, because a mismatch means the
    /// image was truncated or corrupted after linking.
    /// </summary>
    public bool IsValid =>
        HasValidLogo &&
        StoredHeaderChecksum == ComputedHeaderChecksum &&
        StoredGlobalChecksum == ComputedGlobalChecksum &&
        SizeInBytes >= MinimumRomSize &&
        RomSizeMatchesHeader;

    public static RomHeader? Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Parses a ROM image already in memory.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Read"/> so header handling can be tested against
    /// a synthesised buffer, including the malformed cases a real linker would
    /// never produce, without needing GBDK installed.
    /// </remarks>
    public static RomHeader? Parse(byte[] rom) =>
        rom.Length < MinimumRomSize ? null : new RomHeader(rom);

    private static string ReadTitle(byte[] rom)
    {
        Span<byte> title = rom.AsSpan(TitleOffset, 11);
        int length = title.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(title[..(length < 0 ? title.Length : length)]);
    }

    /// <summary>
    /// The header checksum the boot ROM verifies: subtract each header byte and
    /// one more, over 0x134..0x14C.
    /// </summary>
    private static byte ComputeHeaderChecksum(byte[] rom)
    {
        byte checksum = 0;
        for (int address = HeaderChecksumStart; address < HeaderChecksumOffset; address++)
        {
            checksum = unchecked((byte)(checksum - rom[address] - 1));
        }

        return checksum;
    }

    /// <summary>The sum of every byte except the two holding the checksum itself.</summary>
    private static ushort ComputeGlobalChecksum(byte[] rom)
    {
        int sum = 0;
        for (int address = 0; address < rom.Length; address++)
        {
            if (address is GlobalChecksumOffset or GlobalChecksumOffset + 1)
            {
                continue;
            }

            sum += rom[address];
        }

        return unchecked((ushort)sum);
    }

    public override string ToString()
    {
        string mapper = HasMbc ? $", MBC 0x{CartridgeType:X2}, {DeclaredRomBanks} banks" : string.Empty;
        string saveRam = DeclaredRamSize > 0 ? $", {DeclaredRamSize / 1024} KB save RAM" : string.Empty;
        return $"{Title} ({(IsColorEnabled ? "GBC" : "DMG")}, {SizeInBytes / 1024} KB{mapper}{saveRam}, " +
               $"{(IsValid ? "valid" : "INVALID")})";
    }
}
