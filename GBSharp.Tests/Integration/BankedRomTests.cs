using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Integration;

/// <summary>
/// Banked code and data reaching a real cartridge, through real GBDK.
/// </summary>
/// <remarks>
/// Everything up to here can be asserted from the generated C, which proves GB#
/// asked for the right thing. These prove the toolchain agreed: that the linker
/// put the bytes above bank 0, and that the cartridge header describes the image
/// that came out.
/// </remarks>
public sealed class BankedRomTests
{
    private static bool SkipWithoutGbdk() => !TestHarness.GbdkAvailable;

    /// <summary>Enough bytes that the bank cannot be mistaken for empty.</summary>
    private static string Filler(int count) => string.Join(", ", Enumerable.Repeat("1", count));

    private static string BankedProgram(int bank, int bytes) => $$"""
        using GB;

        [Bank({{bank}})]
        public static class Level
        {
            public static readonly byte[] Art = { {{Filler(bytes)}} };

            public static byte First() => Art[0];

            public static byte Last() => Art[{{bytes - 1}}];
        }

        public static class Program
        {
            public static void Main()
            {
                Display.Enable();

                byte a = Level.First();
                byte b = Level.Last();

                while (true)
                {
                    Game.WaitVBlank();
                }
            }
        }
        """;

    /// <summary>
    /// The definition of done for the banking slice.
    /// </summary>
    [Fact]
    public void BankedCodeAndDataLandAboveBankZero()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(BankedProgram(bank: 1, bytes: 6000));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.NotNull(header);

        Assert.True(header!.IsValid, $"header should be valid: {header}");
        Assert.True(header.HasMbc, "a banked ROM needs a mapper to switch with");
        Assert.True(header.RomSizeMatchesHeader, $"file is {header.SizeInBytes}, header says {header.DeclaredRomSize}");

        // The assertion the whole slice exists for: the linker actually placed
        // the data outside the resident bank.
        Assert.NotNull(build.Usage);

        BankUsage bankOne = Assert.Single(build.Usage!.Rom, b => b.BankNumber == 1);
        Assert.True(bankOne.Used > 4096, $"bank 1 holds only {bankOne.Used} bytes");
    }

    /// <summary>
    /// The default cartridge declares battery-backed RAM, so the image has to
    /// actually have some.
    /// </summary>
    /// <remarks>
    /// Byte 0x147 and byte 0x149 come from two different linker flags, and
    /// nothing but the builder makes them agree. They used to disagree: every
    /// banked ROM advertised a battery and reserved nothing, so a host offered
    /// to save and had nowhere to put it.
    /// </remarks>
    [Fact]
    public void ABankedRomReservesTheSaveRamItsHeaderAdvertises()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(BankedProgram(bank: 1, bytes: 6000));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.NotNull(header);

        Assert.Equal(0x1B, header!.CartridgeType);
        Assert.True(
            header.RamMatchesCartridgeType,
            $"cartridge type 0x{header.CartridgeType:X2} with {header.DeclaredRamSize} bytes of save RAM");
        Assert.Equal(8 * 1024, header.DeclaredRamSize);
    }

    /// <summary>
    /// Moving data out of bank 0 has to actually leave bank 0 smaller, or the
    /// attribute is decoration.
    /// </summary>
    [Fact]
    public void BankingMovesBytesOutOfTheResidentBank()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult banked = TestHarness.BuildRom(BankedProgram(bank: 1, bytes: 6000));
        RomBuildResult resident = TestHarness.BuildRom(BankedProgram(bank: 0, bytes: 6000));

        Assert.True(banked.Succeeded, TestHarness.Describe(banked.Diagnostics));
        Assert.True(resident.Succeeded, TestHarness.Describe(resident.Diagnostics));

        int bankedZero = banked.Usage!.Rom.Single(b => b.BankNumber == 0).Used;
        int residentZero = resident.Usage!.Rom.Single(b => b.BankNumber == 0).Used;

        Assert.True(
            residentZero - bankedZero > 5000,
            $"bank 0 went from {residentZero} to {bankedZero} bytes; the 6 KB should have moved");
    }

    [Fact]
    public void AutomaticPlacementProducesAWorkingRom()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom($$"""
            using GB;

            [Bank]
            public static class Level
            {
                public static readonly byte[] Art = { {{Filler(6000)}} };

                public static byte First() => Art[0];
            }

            [Bank]
            public static class Music
            {
                public static readonly byte[] Song = { {{Filler(5000)}} };

                public static byte Start() => Song[0];
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();

                    byte a = Level.First();
                    byte b = Music.Start();

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
                }
            }
            """);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header!.IsValid, $"header should be valid: {header}");

        // bankpack chose; GB# did not say which bank, only that these were not
        // to stay resident. Both had to go somewhere above zero.
        Assert.True(
            build.Usage!.Rom.Count(b => b.BankNumber > 0 && b.Used > 1000) >= 1,
            "expected the packer to fill at least one non-resident bank");
    }

    /// <summary>
    /// The header-only runtime shim is included by every unit, so more units is
    /// more chances for its bare 'inline' functions to collide at link time.
    /// </summary>
    [Fact]
    public void ManyBankedUnitsStillLink()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom($$"""
            using GB;
            using static GB.Hardware;

            [Bank(1)] public static class A { public static void Run() { Display.Enable(); } }
            [Bank(2)] public static class B { public static void Run() { Display.ShowSprites(); } }
            [Bank(3)] public static class C { public static void Run() { Sprites.Move(0, 1, 2); } }

            public static class Program
            {
                public static void Main()
                {
                    A.Run();
                    B.Run();
                    C.Run();

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
                }
            }
            """);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        // header + game.c + three bank units.
        Assert.Equal(5, build.GeneratedFiles.Count);
        Assert.True(RomHeader.Read(build.RomPath!)!.IsValid);
    }
}
