using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Backend;

/// <summary>
/// Spotting a bank 0 that the linker ran past the end of. These run without
/// GBDK: the area tables below are real sdld output, trimmed to the area lines
/// and kept as literals so the format being relied on is visible in the test.
/// </summary>
public sealed class ResidentOverflowReaderTests
{
    /// <summary>
    /// A ROM that fits, from a build whose bank 0 ends at 0x3CC5. The banked
    /// area sits at a flat 0x14000 and the RAM areas above 0xA000, which is
    /// what the resident test excludes them by.
    /// </summary>
    private static readonly string[] FittingMap =
    [
        "Area                       Addr        Size        Decimal Bytes (Attributes)",
        "--------------------       ----        ----        ------- ----- ------------",
        "_CODE                  00000200    000033DE =       13278. bytes (REL,CON)",
        "_HOME                  000035DE    000006C9 =        1737. bytes (REL,CON)",
        "_INITIALIZER           00003CA7    00000011 =          17. bytes (REL,CON)",
        "_GSINIT                00003CB8    0000000C =          12. bytes (REL,CON)",
        "_GSFINAL               00003CC4    00000001 =           1. bytes (REL,CON)",
        "_CODE_1                00014000    0000044C =        1100. bytes (REL,CON)",
        "_DATA                  0000C0A0    00000263 =         611. bytes (REL,CON)",
        "_HRAM                  0000FF80    00000013 =          19. bytes (REL,CON)",
    ];

    [Fact]
    public void ARomThatFitsReportsNothing()
    {
        Assert.Null(ResidentOverflowReader.Parse(FittingMap));
    }

    [Fact]
    public void BankedAndRamAreasAreNeverMistakenForResidentOnes()
    {
        // _CODE_1's flat address is past the end of every ROM bank the check
        // cares about, and _DATA/_HRAM are not ROM at all. If either were
        // treated as resident, the fitting map above would report an overflow.
        Assert.Null(ResidentOverflowReader.Parse(
            FittingMap.Where(l => !l.StartsWith("_CODE ", StringComparison.Ordinal)).ToArray()));
    }

    [Fact]
    public void AnAreaRunningThroughTheBoundaryIsReported()
    {
        // The real failure: _CODE runs from 0x0200 to 0x48D9 and the chain ends
        // at 0x4FC0, so 4032 bytes sit where the switchable bank appears.
        string[] map =
        [
            "_CODE                  00000200    000046D9 =       18137. bytes (REL,CON)",
            "_HOME                  000048D9    000006C9 =        1737. bytes (REL,CON)",
            "_INITIALIZER           00004FA2    00000011 =          17. bytes (REL,CON)",
            "_GSINIT                00004FB3    0000000C =          12. bytes (REL,CON)",
            "_GSFINAL               00004FBF    00000001 =           1. bytes (REL,CON)",
            "_CODE_1                00014000    0000044C =        1100. bytes (REL,CON)",
        ];

        ResidentOverflow? overflow = ResidentOverflowReader.Parse(map);

        Assert.NotNull(overflow);
        Assert.Equal("_CODE", overflow.Crossing.Name);
        Assert.Equal(0x0200, overflow.Crossing.Start);
        Assert.Equal(0x48D9, overflow.Crossing.End);
        Assert.Equal(0x4FC0, overflow.ResidentEnd);
        Assert.Equal(0x4FC0 - 0x4000, overflow.Bytes);
    }

    [Fact]
    public void TheBoundaryFallingBetweenTwoAreasIsStillAnOverflow()
    {
        // _CODE ends exactly at 0x4000, so nothing straddles: every displaced
        // byte belongs to an area that starts past the boundary. Checking
        // where the chain ends catches this; checking each area for straddling
        // does not.
        string[] map =
        [
            "_CODE                  00000200    00003E00 =       15872. bytes (REL,CON)",
            "_HOME                  00004000    000006C9 =        1737. bytes (REL,CON)",
        ];

        ResidentOverflow? overflow = ResidentOverflowReader.Parse(map);

        Assert.NotNull(overflow);
        Assert.Equal("_HOME", overflow.Crossing.Name);
        Assert.Equal(0x46C9 - 0x4000, overflow.Bytes);
    }

    [Fact]
    public void AnAreaEndingExactlyAtTheBoundaryFits()
    {
        // 0x4000 is the first address past bank 0, so ending there is the
        // largest a resident chain is allowed to be, not one byte too far.
        Assert.Null(ResidentOverflowReader.Parse(
            ["_CODE                  00000200    00003E00 =       15872. bytes (REL,CON)"]));
    }

    [Fact]
    public void RepeatedAreaLinesNameTheEarliestCrossing()
    {
        // sdld lists an area once per contributing module, and the build report
        // should name the area the boundary is inside rather than whichever
        // repeat happened to be read last.
        string[] map =
        [
            "_CODE                  00000200    000046D9 =       18137. bytes (REL,CON)",
            "_CODE                  00000200    000046D9 =       18137. bytes (REL,CON)",
            "_HOME                  000048D9    000006C9 =        1737. bytes (REL,CON)",
        ];

        ResidentOverflow? overflow = ResidentOverflowReader.Parse(map);

        Assert.NotNull(overflow);
        Assert.Equal("_CODE", overflow.Crossing.Name);
    }

    [Fact]
    public void AMissingMapIsNotAFailure()
    {
        // The ROM is written before the map is read, and GBS0510 already covers
        // the map being unavailable.
        Assert.Null(ResidentOverflowReader.Read(
            Path.Combine(Path.GetTempPath(), "gbsharp-no-such-map-" + Guid.NewGuid().ToString("N") + ".map")));
    }
}
