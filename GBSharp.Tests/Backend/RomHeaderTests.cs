using GBSharp.Backend.GBDK;

namespace GBSharp.Tests.Backend;

/// <summary>
/// Cartridge header parsing, against synthesised images.
/// </summary>
/// <remarks>
/// These need no toolchain: the point is to cover the malformed cases a real
/// linker would never produce, which is exactly what a ROM built by GBDK cannot
/// demonstrate.
/// </remarks>
public sealed class RomHeaderTests
{
    private static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    /// <summary>
    /// A ROM the boot ROM would accept, with the checksums made to agree.
    /// </summary>
    private static byte[] Rom(
        int sizeInBytes,
        byte cartridgeType,
        byte romSizeCode,
        string title = "TEST",
        byte ramSizeCode = 0x00)
    {
        var rom = new byte[sizeInBytes];

        NintendoLogo.CopyTo(rom, 0x104);
        System.Text.Encoding.ASCII.GetBytes(title).CopyTo(rom, 0x134);
        rom[0x147] = cartridgeType;
        rom[0x148] = romSizeCode;
        rom[0x149] = ramSizeCode;

        byte header = 0;
        for (int address = 0x134; address < 0x14D; address++)
        {
            header = unchecked((byte)(header - rom[address] - 1));
        }

        rom[0x14D] = header;

        int sum = 0;
        for (int address = 0; address < rom.Length; address++)
        {
            if (address is 0x14E or 0x14F)
            {
                continue;
            }

            sum += rom[address];
        }

        rom[0x14E] = (byte)(sum >> 8);
        rom[0x14F] = (byte)sum;

        return rom;
    }

    [Fact]
    public void AnUnbankedRomIsValidAndDeclaresNoMapper()
    {
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x8000, 0x00, 0x00)));

        Assert.True(header.IsValid);
        Assert.False(header.HasMbc);
        Assert.Equal(2, header.DeclaredRomBanks);
        Assert.Equal(0x8000, header.DeclaredRomSize);
        Assert.Equal("TEST", header.Title);
    }

    [Fact]
    public void TheCartridgeTypeAndBankCountAreRead()
    {
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x10000, 0x1B, 0x01)));

        Assert.True(header.IsValid);
        Assert.True(header.HasMbc);
        Assert.Equal(0x1B, header.CartridgeType);
        Assert.Equal(4, header.DeclaredRomBanks);
        Assert.Equal(0x10000, header.DeclaredRomSize);
    }

    /// <summary>
    /// The check that earns its keep once banking exists: a wrong bank count or
    /// an image truncated after linking makes the file disagree with its header.
    /// </summary>
    [Fact]
    public void ARomSmallerThanItsHeaderClaimsIsInvalid()
    {
        // Declares 64 KB, is 32 KB.
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x8000, 0x1B, 0x01)));

        Assert.False(header.RomSizeMatchesHeader);
        Assert.False(header.IsValid);
    }

    [Fact]
    public void ANonsenseSizeCodeDoesNotOverflow()
    {
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x8000, 0x1B, 0xFF)));

        // Clamped rather than shifted by 255.
        Assert.Equal(512, header.DeclaredRomBanks);
        Assert.False(header.IsValid);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x02, 8 * 1024)]
    [InlineData(0x03, 32 * 1024)]
    [InlineData(0x04, 128 * 1024)]
    [InlineData(0x05, 64 * 1024)]
    // 0x01 was 2 KB on hardware never produced, and anything above the table is
    // not a size at all. Both read as no save RAM rather than as a guess.
    [InlineData(0x01, 0)]
    [InlineData(0x77, 0)]
    public void TheSaveRamSizeIsDecodedFromItsOwnTable(byte code, int expected)
    {
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x10000, 0x1B, 0x01, ramSizeCode: code)));

        Assert.Equal(code, header.RamSizeCode);
        Assert.Equal(expected, header.DeclaredRamSize);
    }

    /// <summary>
    /// The check the Banking sample failed: 0x147 said battery-backed RAM and
    /// 0x149 said none, so the cartridge advertised a save with nothing behind it.
    /// </summary>
    [Fact]
    public void AMapperThatNamesRamWithNoneReservedIsCaught()
    {
        RomHeader lying = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x10000, 0x1B, 0x01, ramSizeCode: 0x00)));

        Assert.False(lying.RamMatchesCartridgeType);

        // Still a ROM the boot ROM would run: hardware never reads 0x149, which
        // is exactly why this needs saying out loud rather than failing IsValid.
        Assert.True(lying.IsValid);
    }

    [Fact]
    public void RamReservedByACartridgeWithNoRamIsAlsoCaught()
    {
        // MBC5 with no RAM in the type byte, 8 KB reserved at 0x149.
        RomHeader header = Assert.IsType<RomHeader>(RomHeader.Parse(Rom(0x10000, 0x19, 0x01, ramSizeCode: 0x02)));

        Assert.False(header.RamMatchesCartridgeType);
    }

    [Theory]
    [InlineData(0x00, 0x00)] // no mapper, no RAM
    [InlineData(0x19, 0x00)] // MBC5, no RAM
    [InlineData(0x1B, 0x02)] // MBC5+RAM+BATTERY with a bank behind it
    [InlineData(0x13, 0x03)] // MBC3+RAM+BATTERY, a mapper GB# does not emit
    public void AConsistentHeaderIsAccepted(byte cartridgeType, byte ramSizeCode)
    {
        RomHeader header = Assert.IsType<RomHeader>(
            RomHeader.Parse(Rom(0x10000, cartridgeType, 0x01, ramSizeCode: ramSizeCode)));

        Assert.True(header.RamMatchesCartridgeType);
    }

    [Fact]
    public void ABufferTooSmallToHoldAHeaderIsRejected() =>
        Assert.Null(RomHeader.Parse(new byte[0x1000]));

    [Fact]
    public void ToStringNamesTheMapperOnlyWhenThereIsOne()
    {
        Assert.DoesNotContain("MBC", RomHeader.Parse(Rom(0x8000, 0x00, 0x00))!.ToString(), StringComparison.Ordinal);
        Assert.Contains("MBC 0x1B", RomHeader.Parse(Rom(0x10000, 0x1B, 0x01))!.ToString(), StringComparison.Ordinal);
    }
}
