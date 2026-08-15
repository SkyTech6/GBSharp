using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Backend;

/// <summary>
/// Totalling per-address counters into per-method costs. Runs without GBDK and
/// without the emulator: the counters are written here by hand, in the shape
/// the ABI hands them over, so what the report assumes about them is visible.
/// </summary>
public sealed class ProfileReportTests : IDisposable
{
    private const string SymbolMap = """
        00:0150 _main
        03:4000 _PlayerSystem_Update
        03:4100 _EnemySystem_Update
        """;

    private const string FunctionMap = """
        [
          { "name": "main", "method": "Program.Main()", "file": "C:\\game\\Program.cs", "line": 7 },
          { "name": "PlayerSystem_Update", "method": "PlayerSystem.Update()", "file": "C:\\game\\PlayerSystem.cs", "line": 18 },
          { "name": "EnemySystem_Update", "method": "EnemySystem.Update()", "file": "C:\\game\\EnemySystem.cs", "line": 42 }
        ]
        """;

    private readonly string directory =
        Directory.CreateTempSubdirectory("gbsharp-profile-").FullName;

    private RomSymbolResolver Resolver()
    {
        string symbolPath = Path.Combine(directory, "game.sym");
        string functionPath = Path.Combine(directory, "game.functions.json");
        File.WriteAllText(symbolPath, SymbolMap);
        File.WriteAllText(functionPath, FunctionMap);
        return RomSymbolResolver.Load(symbolPath, functionPath);
    }

    /// <summary>
    /// A ROM address is the offset into the cartridge file. Bank 0 is mapped at
    /// 0x0000 and every other bank at 0x4000, which is the arithmetic the
    /// report has to get right for any of this to mean anything.
    /// </summary>
    private static int RomAddress(int bank, ushort address) =>
        (bank * 0x4000) + (bank == 0 ? address : address - 0x4000);

    [Fact]
    public void TicksAreTotalledIntoTheMethodTheyWereSpentIn()
    {
        var counts = new uint[4 * 0x4000];
        var cycles = new uint[4 * 0x4000];

        // Three instructions inside EnemySystem_Update, which starts at 0x4100.
        foreach (ushort address in new ushort[] { 0x4100, 0x4104, 0x4110 })
        {
            counts[RomAddress(3, address)] = 10;
            cycles[RomAddress(3, address)] = 100;
        }

        // One cheap instruction in PlayerSystem_Update, run far more often.
        counts[RomAddress(3, 0x4000)] = 5000;
        cycles[RomAddress(3, 0x4000)] = 20;

        ProfileReport report = ProfileReport.Build(Resolver(), counts, cycles);

        ProfileEntry top = report.Entries[0];
        Assert.Equal("EnemySystem.Update()", top.Method);
        Assert.Equal(300, top.Cycles);
        Assert.Equal(30, top.Count);

        // The whole reason the report ranks by ticks and not by count: the
        // busiest function is not the expensive one, and ranking by count would
        // put the cheap loop on top and send the developer to the wrong file.
        Assert.Equal("PlayerSystem.Update()", report.Entries[1].Method);
        Assert.True(report.Entries[1].Count > top.Count);
    }

    [Fact]
    public void CallsAreCountedFromTheSymbolsOwnFirstAddress()
    {
        var counts = new uint[4 * 0x4000];
        var cycles = new uint[4 * 0x4000];

        // Entered 12 times; its body ran far more instructions than that.
        counts[RomAddress(3, 0x4100)] = 12;
        counts[RomAddress(3, 0x4104)] = 480;
        cycles[RomAddress(3, 0x4100)] = 48;
        cycles[RomAddress(3, 0x4104)] = 1920;

        ProfileEntry entry = Assert.Single(ProfileReport.Build(Resolver(), counts, cycles).Entries);

        // The first instruction of a function runs exactly once per call, so
        // the count there is the call count -- no call stack needed.
        Assert.Equal(12, entry.Invocations);
        Assert.Equal(492, entry.Count);
        Assert.Equal(1968 / 12.0, entry.CyclesPerInvocation, 3);
    }

    [Fact]
    public void CodeEnteredOnlyBelowItsTopIsNotCountedAsCalled()
    {
        var cycles = new uint[4 * 0x4000];
        var counts = new uint[4 * 0x4000];

        // Nothing ran at 0x4100 itself, so nothing entered EnemySystem_Update
        // from the top. Reporting a call count here would be inventing one.
        counts[RomAddress(3, 0x4108)] = 99;
        cycles[RomAddress(3, 0x4108)] = 400;

        ProfileEntry entry = Assert.Single(ProfileReport.Build(Resolver(), counts, cycles).Entries);

        Assert.Equal(0, entry.Invocations);
        Assert.Equal(0, entry.CyclesPerInvocation);
    }

    [Fact]
    public void TheFrameBudgetIsExpressedAgainstOneFramesTicks()
    {
        var cycles = new uint[4 * 0x4000];

        // Half of one frame's 70224 ticks, over 10 frames.
        cycles[RomAddress(3, 0x4100)] = 70224 / 2 * 10;

        ProfileReport report = ProfileReport.Build(Resolver(), [], cycles, frames: 10);

        Assert.Equal(0.5, report.FrameBudgetShare(report.Entries[0]), 3);
        Assert.Equal(70224 / 2.0, report.CyclesPerFrame(report.Entries[0]), 1);
    }

    [Fact]
    public void AddressesTheMapDoesNotCoverAreReportedRatherThanDropped()
    {
        var cycles = new uint[4 * 0x4000];

        cycles[RomAddress(3, 0x4100)] = 1000;

        // Bank 1 holds no symbols at all, so nothing covers this.
        cycles[RomAddress(1, 0x4020)] = 250;

        ProfileReport report = ProfileReport.Build(Resolver(), [], cycles);

        Assert.Equal(1250, report.TotalCycles);
        Assert.Equal(250, report.UnattributedCycles);

        // Renormalising so the attributed shares summed to 100% would hide
        // exactly the case this matters in: a map that does not match the ROM.
        Assert.Contains("no symbol", report.Describe());
    }

    [Fact]
    public void BankZeroIsAddressedFromZeroAndOtherBanksFrom0x4000()
    {
        var cycles = new uint[4 * 0x4000];

        cycles[RomAddress(0, 0x0150)] = 700;

        ProfileReport report = ProfileReport.Build(Resolver(), [], cycles);

        // If the two halves of the mapping were confused, this would land in
        // bank 0 at 0x4150 and resolve to nothing at all.
        Assert.Equal("Program.Main()", Assert.Single(report.Entries).Method);
    }

    [Fact]
    public void AnEmptyProfileSaysWhyItIsEmpty()
    {
        ProfileReport report = ProfileReport.Build(Resolver(), [], []);

        Assert.Empty(report.Entries);
        Assert.Contains("debug flavour", report.Describe());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
