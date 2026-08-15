using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Backend;

/// <summary>
/// Parsing GBDK's romusage output. These run without GBDK: the payload below is
/// real output from romusage 4.5.0, kept here as a literal rather than a golden
/// file so the shape being relied on is visible in the test.
/// </summary>
public sealed class RomUsageReaderTests
{
    private const string SampleJson = """
        {"banks":
          [
            {
            "name":         "ROM_0",
            "type":         "0",
            "baseBankNum":  "0",
            "isBanked":     "0",
            "isMergedBank": "0",
            "rangeStart":   "0",
            "rangeEnd":     "16383",
            "size":         "16384",
            "used":         "391",
            "free":         "15993",
            "usedPercent":  "2",
            "freePercent":  "98",
            "miniGraph":    "-..........................."
            }
            ,
            {
            "name":         "WRAM_LO",
            "type":         "3",
            "baseBankNum":  "0",
            "isBanked":     "0",
            "isMergedBank": "0",
            "rangeStart":   "49152",
            "rangeEnd":     "53247",
            "size":         "4096",
            "used":         "49",
            "free":         "4047",
            "usedPercent":  "1",
            "freePercent":  "99",
            "miniGraph":    ".-.........................."
            }
          ]
        }
        """;

    [Fact]
    public void NumbersArriveAsStringsAndAreParsedAnyway()
    {
        // Every numeric field in romusage's JSON is quoted. Deserialising
        // straight into an int compiles, looks right, and throws at runtime.
        Assert.True(RomUsageReader.TryParse(SampleJson, out RomUsageReport? report, out string? failure), failure);

        BankUsage rom = Assert.Single(report!.Rom);
        Assert.Equal(16384, rom.Size);
        Assert.Equal(391, rom.Used);
        Assert.Equal(15993, rom.Free);
        Assert.Equal(2, rom.UsedPercent);
    }

    [Fact]
    public void BanksAreBucketedByRegion()
    {
        Assert.True(RomUsageReader.TryParse(SampleJson, out RomUsageReport? report, out _));

        Assert.Equal("ROM_0", Assert.Single(report!.Rom).Name);
        Assert.Equal("WRAM_LO", Assert.Single(report.Wram).Name);
        Assert.Equal(49, report.WramUsed);
        Assert.Equal(4096, report.WramSize);
    }

    /// <summary>
    /// Real output from a two-bank ROM, captured from romusage 4.5.0.
    /// </summary>
    /// <remarks>
    /// Note what is absent: this ROM was linked with four banks reserved, but
    /// only the two with content appear. Anything joining declared placement
    /// against this report has to tolerate a bank with no row.
    /// </remarks>
    private const string MultiBankJson = """
        {"banks":
          [
            {
            "name":         "ROM_0",
            "type":         "0",
            "baseBankNum":  "0",
            "isBanked":     "0",
            "isMergedBank": "0",
            "rangeStart":   "0",
            "rangeEnd":     "16383",
            "size":         "16384",
            "used":         "426",
            "free":         "15958",
            "usedPercent":  "3",
            "freePercent":  "97",
            "miniGraph":    "--.........................."
            }
            ,
            {
            "name":         "ROM_1",
            "type":         "0",
            "baseBankNum":  "1",
            "isBanked":     "1",
            "isMergedBank": "0",
            "rangeStart":   "16384",
            "rangeEnd":     "32767",
            "size":         "16384",
            "used":         "4184",
            "free":         "12200",
            "usedPercent":  "26",
            "freePercent":  "74",
            "miniGraph":    "#######....................."
            }
            ,
            {
            "name":         "WRAM_LO",
            "type":         "3",
            "baseBankNum":  "0",
            "isBanked":     "0",
            "isMergedBank": "0",
            "rangeStart":   "49152",
            "rangeEnd":     "53247",
            "size":         "4096",
            "used":         "19",
            "free":         "4077",
            "usedPercent":  "0",
            "freePercent":  "100",
            "miniGraph":    "............................"
            }
          ]
        }
        """;

    [Fact]
    public void EveryRomBankIsParsedAndNumbered()
    {
        Assert.True(RomUsageReader.TryParse(MultiBankJson, out RomUsageReport? report, out string? failure), failure);

        BankUsage[] rom = [.. report!.Rom.OrderBy(b => b.BankNumber)];

        Assert.Equal(2, rom.Length);

        Assert.Equal(0, rom[0].BankNumber);
        Assert.Equal(426, rom[0].Used);

        // A banked ROM_1 must still bucket as ROM, which the name prefix decides.
        Assert.Equal(1, rom[1].BankNumber);
        Assert.Equal(4184, rom[1].Used);
        Assert.Equal(16384, rom[1].Size);

        // GB# computes the percentage from used and size rather than reading
        // romusage's own "usedPercent", and truncates where romusage rounds:
        // 4184/16384 is 25.5%, reported here as 25 and by romusage as 26.
        // Depending on two fields when one is derivable is the worse trade, and
        // erring low only ever delays a "nearly full" notice by half a percent.
        Assert.Equal(25, rom[1].UsedPercent);
    }

    /// <summary>
    /// Real output from a ROM with code in bank 0 and data in bank 2.
    /// </summary>
    /// <remarks>
    /// Note <c>ROM_2</c> carrying <c>baseBankNum</c> 1: that field counts the
    /// areas present rather than naming the bank, so it disagrees with the name
    /// the moment a bank is skipped. The name is the authority.
    /// </remarks>
    private const string SkippedBankJson = """
        {"banks":
          [
            {
            "name":         "ROM_0",
            "baseBankNum":  "0",
            "rangeStart":   "0",
            "size":         "16384",
            "used":         "746"
            }
            ,
            {
            "name":         "ROM_2",
            "baseBankNum":  "1",
            "rangeStart":   "16384",
            "size":         "16384",
            "used":         "888"
            }
            ,
            {
            "name":         "WRAM_LO",
            "baseBankNum":  "0",
            "rangeStart":   "49152",
            "size":         "4096",
            "used":         "18"
            }
          ]
        }
        """;

    [Fact]
    public void TheBankNumberComesFromTheAreaNameNotBaseBankNum()
    {
        Assert.True(RomUsageReader.TryParse(SkippedBankJson, out RomUsageReport? report, out string? failure), failure);

        BankUsage banked = Assert.Single(report!.Rom, b => b.Name == "ROM_2");

        // baseBankNum says 1. Believing it reported bank 2's contents as bank 1.
        Assert.Equal(2, banked.BankNumber);
        Assert.Equal(888, banked.Used);
    }

    [Fact]
    public void AnAreaWithNoNumberInItsNameFallsBackToBaseBankNum()
    {
        Assert.True(RomUsageReader.TryParse(SkippedBankJson, out RomUsageReport? report, out _));

        Assert.Equal(0, Assert.Single(report!.Wram).BankNumber);
    }

    [Fact]
    public void EmptyBanksAreAbsentRatherThanZeroed()
    {
        Assert.True(RomUsageReader.TryParse(MultiBankJson, out RomUsageReport? report, out _));

        // Linked with four banks reserved; banks 2 and 3 hold nothing and so are
        // not reported at all.
        Assert.DoesNotContain(report!.Rom, b => b.BankNumber > 1);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"banks": "no"}""")]
    public void MalformedOutputFailsWithoutThrowing(string json)
    {
        // A report that cannot be read must never fail a build that succeeded.
        Assert.False(RomUsageReader.TryParse(json, out RomUsageReport? report, out string? failure));
        Assert.Null(report);
        Assert.NotNull(failure);
    }
}
