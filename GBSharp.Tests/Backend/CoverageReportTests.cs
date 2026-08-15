using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Backend;

/// <summary>
/// Which of a ROM's code a session never reached. Runs without GBDK and
/// without the emulator: the usage flags are written here by hand, in the shape
/// the ABI hands them over.
/// </summary>
public sealed class CoverageReportTests : IDisposable
{
    private const byte Code = 0x1;
    private const byte CodeStart = 0x4;

    private const string SymbolMap = """
        03:4000 _PlayerSystem_Update
        03:4100 _EnemySystem_Update
        03:4200 _BossSystem_Update
        """;

    private const string FunctionMap = """
        [
          { "name": "PlayerSystem_Update", "method": "PlayerSystem.Update()", "file": "C:\\game\\PlayerSystem.cs", "line": 18 },
          { "name": "EnemySystem_Update", "method": "EnemySystem.Update()", "file": "C:\\game\\EnemySystem.cs", "line": 42 },
          { "name": "BossSystem_Update", "method": "BossSystem.Update()", "file": "C:\\game\\BossSystem.cs", "line": 9 }
        ]
        """;

    private readonly string directory =
        Directory.CreateTempSubdirectory("gbsharp-coverage-").FullName;

    private RomSymbolResolver Resolver()
    {
        File.WriteAllText(Path.Combine(directory, "game.sym"), SymbolMap);
        File.WriteAllText(Path.Combine(directory, "game.functions.json"), FunctionMap);
        return RomSymbolResolver.Load(
            Path.Combine(directory, "game.sym"),
            Path.Combine(directory, "game.functions.json"));
    }

    private static int RomAddress(int bank, ushort address) =>
        (bank * 0x4000) + (bank == 0 ? address : address - 0x4000);

    /// <summary>Marks an instruction of <paramref name="length"/> bytes as executed.</summary>
    private static void Execute(byte[] usage, int bank, ushort address, int length = 1)
    {
        usage[RomAddress(bank, address)] |= Code | CodeStart;

        for (int i = 1; i < length; i++)
        {
            usage[RomAddress(bank, address) + i] |= Code;
        }
    }

    [Fact]
    public void AMethodNothingReachedIsNamed()
    {
        var usage = new byte[4 * 0x4000];

        Execute(usage, 3, 0x4000);
        Execute(usage, 3, 0x4100);
        // BossSystem_Update at 0x4200 is never touched.

        CoverageReport report = CoverageReport.Build(Resolver(), usage);

        CoverageEntry entry = Assert.Single(report.Unreached);
        Assert.Equal("BossSystem.Update()", entry.Method);
        Assert.Contains("BossSystem.Update()", report.Describe());
    }

    [Fact]
    public void EveryMethodReachedIsSaidPlainly()
    {
        var usage = new byte[4 * 0x4000];

        Execute(usage, 3, 0x4000);
        Execute(usage, 3, 0x4100);
        Execute(usage, 3, 0x4200);

        CoverageReport report = CoverageReport.Build(Resolver(), usage);

        Assert.Empty(report.Unreached);
        Assert.Contains("Every method GB# emitted was entered", report.Describe());
    }

    [Fact]
    public void InstructionsAreCountedByOpcodeStartsNotByBytes()
    {
        var usage = new byte[4 * 0x4000];

        // Two instructions, one of them three bytes long. Four bytes of code,
        // two instructions -- counting bytes would say four and overstate how
        // much of the function ran.
        Execute(usage, 3, 0x4100, length: 3);
        Execute(usage, 3, 0x4103);

        CoverageEntry entry = Assert.Single(
            CoverageReport.Build(Resolver(), usage).Entries,
            e => e.Symbol == "EnemySystem_Update");

        Assert.Equal(2, entry.Instructions);
        Assert.Equal(4, entry.ExecutedBytes);
    }

    [Fact]
    public void ABytePartlyReachedIsPartlyCovered()
    {
        var usage = new byte[4 * 0x4000];

        // EnemySystem_Update runs from 0x4100 to 0x4200, so 256 bytes, of which
        // 64 ran.
        for (ushort address = 0x4100; address < 0x4140; address++)
        {
            usage[RomAddress(3, address)] |= Code;
        }

        CoverageEntry entry = Assert.Single(
            CoverageReport.Build(Resolver(), usage).Entries,
            e => e.Symbol == "EnemySystem_Update");

        Assert.Equal(256, entry.Bytes);
        Assert.Equal(64, entry.ExecutedBytes);
        Assert.Equal(0.25, entry.Share, 3);
    }

    [Fact]
    public void ReadingAsDataIsNotExecuting()
    {
        var usage = new byte[4 * 0x4000];

        // A jump table inside BossSystem_Update, read but never run. The
        // difference matters: data that is read is reached, but the code around
        // it may never have executed, and only executing counts as coverage.
        usage[RomAddress(3, 0x4200)] |= 0x2;

        CoverageReport report = CoverageReport.Build(Resolver(), usage);

        Assert.Contains(report.Unreached, e => e.Symbol == "BossSystem_Update");
    }

    [Fact]
    public void TheHeadlineCountsSymbolsRatherThanClaimingAPercentageOfTheRom()
    {
        var usage = new byte[4 * 0x4000];
        Execute(usage, 3, 0x4000);

        string text = CoverageReport.Build(Resolver(), usage).Describe();

        // The last symbol in a bank is charged with the bank's padding, so a
        // byte percentage would say more about how empty the ROM is than about
        // what ran. Reporting one would be reporting a number that improves
        // when you add code.
        Assert.Contains("Reached 1 of 3 symbols", text);
        Assert.DoesNotContain("%", text);
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
