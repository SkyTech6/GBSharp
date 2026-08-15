using System.Text.Json;
using GBSharp.Backend.GBDK;
using GBSharp.Cli;
using GBSharp.Cli.Reporting;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Cli;

/// <summary>
/// The build report, in both the forms it is published in.
/// </summary>
/// <remarks>
/// <para>
/// Neither path had a test before this. That is how the two drifted: the record
/// says it exists so "the JSON and the terminal cannot disagree", while the
/// console computed its own declared-WRAM and declared-ROM figures alongside it.
/// The console now renders from the record, and the agreement is asserted here
/// rather than assumed in a comment.
/// </para>
/// <para>
/// One class, because <see cref="Console.SetOut"/> is process-global and xUnit
/// runs separate classes in parallel.
/// </para>
/// </remarks>
public sealed class ReportTests
{
    private const string Recursive = """
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            public static void Main()
            {
                Helpers.Down(3);
            }
        }

        public static class Helpers
        {
            public static void Down(byte n)
            {
                if (n > 0) { Down((byte)(n - 1)); }
            }
        }
        """;

    private static BuildReport Report(IRModule module) =>
        BuildReport.Create(module, romPath: null, GBTarget.GameBoy, [], usage: null, gbdkVersion: null);

    /// <summary>Renders the console report and returns what it printed.</summary>
    private static string Console(IRModule module)
    {
        BuildReport report = Report(module);

        var buffer = new StringWriter();
        TextWriter original = System.Console.Out;

        try
        {
            System.Console.SetOut(buffer);

            // A path that does not exist is fine: nothing reads the file unless
            // the program declares a budget or uses a bank, and this one does not.
            ConsoleReporter.WriteBuildReport(
                report,
                module,
                Path.Combine(Path.GetTempPath(), "gbsharp-tests", "absent.gb"),
                GBTarget.GameBoy,
                []);
        }
        finally
        {
            System.Console.SetOut(original);
        }

        return buffer.ToString();
    }

    private static IRModule Simple() => TestHarness.CompileModule(TestHarness.Program("""
                while (true)
                {
                    for (byte i = 0; i < 40; i++) { Sprites[0].X = i; }
                    Game.WaitVBlank();
                }
        """));

    // -----------------------------------------------------------------------
    // JSON
    // -----------------------------------------------------------------------

    [Fact]
    public void TheReportRoundTripsThroughItsSerializer()
    {
        BuildReport report = Report(Simple());

        string json = JsonSerializer.Serialize(report, BuildReportJson.Default.BuildReport);
        BuildReport? read = JsonSerializer.Deserialize(json, BuildReportJson.Default.BuildReport);

        Assert.NotNull(read);
        Assert.Equal(report.Memory.DeclaredWram, read.Memory.DeclaredWram);
        Assert.Equal(report.Cycles?.FrameCycles, read.Cycles?.FrameCycles);
        Assert.Equal(report.Stack?.Calls, read.Stack?.Calls);
    }

    /// <summary>
    /// The cycle and stack sections are additive and nullable, so a script
    /// written against version 1 reads exactly what it did before them.
    /// </summary>
    [Fact]
    public void AddingCostsDidNotBumpTheSchemaVersion()
    {
        Assert.Equal(1, BuildReport.CurrentSchemaVersion);
        Assert.Equal(1, Report(Simple()).SchemaVersion);
    }

    /// <summary>
    /// Absent rather than zeroed, so a consumer can tell "GB# had nothing to say"
    /// from "GB# said zero".
    /// </summary>
    [Fact]
    public void AModuleWithNoCostsOmitsTheCostSectionsEntirely()
    {
        BuildReport report = Report(Simple() with { Costs = ModuleCostReport.Empty });

        Assert.Null(report.Cycles);
        Assert.Null(report.Stack);

        string json = JsonSerializer.Serialize(report, BuildReportJson.Default.BuildReport);

        Assert.DoesNotContain("\"cycles\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stack\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameBudgetIsTheHardwareFigure() =>
        Assert.Equal(70_224, Report(Simple()).Cycles!.FrameCycles);

    /// <summary>
    /// A ranking is for finding where the time went. Generated collection
    /// operations are real costs, but listing code the developer did not write
    /// above the code they did does not answer that question.
    /// </summary>
    [Fact]
    public void TheRankingLeavesOutCompilerGeneratedFunctions()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Data.Items.Add(1); Data.Items.Add(2);",
            """
            public static class Data
            {
                [Capacity(8)]
                public static FixedList<byte> Items;
            }
            """));

        Assert.Contains(module.Costs.Functions, f => f.IsCompilerGenerated);

        Assert.DoesNotContain(
            Report(module).Cycles?.Functions ?? [],
            f => f.Name.Contains("FixedList", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Console
    // -----------------------------------------------------------------------

    [Fact]
    public void TheConsoleShowsTheCycleSection()
    {
        string output = Console(Simple());

        Assert.Contains("Cycle estimates", output, StringComparison.Ordinal);
        Assert.Contains("70,224 cycles", output, StringComparison.Ordinal);
        Assert.Contains("Frame loop", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The caveat is the reason the section is allowed to print numbers at all,
    /// so it is not optional decoration.
    /// </summary>
    [Fact]
    public void TheCycleSectionSaysTheNumbersAreEstimates()
    {
        string output = Console(Simple());

        Assert.Contains("Estimated statically from the IR", output, StringComparison.Ordinal);
        Assert.Contains("not as", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConsoleShowsTheDeepestCallPath()
    {
        string output = Console(TestHarness.CompileModule(TestHarness.Program(
            "        Helpers.Step();",
            """
            public static class Helpers
            {
                public static void Step() { Sprites[0].X = 1; }
            }
            """)));

        Assert.Contains("Call stack", output, StringComparison.Ordinal);
        Assert.Contains("->", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recursive program has no maximum depth. Printing the acyclic figure here
    /// would undo what GBS0058 just explained.
    /// </summary>
    [Fact]
    public void ARecursiveProgramReportsAnUnboundedStack()
    {
        string output = Console(TestHarness.CompileModule(Recursive));

        Assert.Contains("unbounded (recursive)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression guard: a project that has nothing to report gets the report
    /// it got before these sections existed.
    /// </summary>
    [Fact]
    public void AModuleWithNoCostsPrintsNoNewSections()
    {
        string output = Console(Simple() with { Costs = ModuleCostReport.Empty });

        Assert.DoesNotContain("Cycle estimates", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Call stack", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drift this whole unification was for. Both paths quote the same
    /// declared figures because there is now one place that computes them.
    /// </summary>
    [Fact]
    public void TheConsoleAndTheJsonQuoteTheSameDeclaredMemory()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Data.Counter++;",
            """
            public static class Data
            {
                public static ushort Counter;

                public static readonly byte[] Table = { 1, 2, 3, 4 };
            }
            """));

        BuildReport report = Report(module);

        Assert.Equal(2, report.Memory.DeclaredWram);
        Assert.Equal(4, report.Memory.DeclaredRom);

        string output = Console(module);

        Assert.Contains("Static objects (declared) 2 B", output, StringComparison.Ordinal);
        Assert.Contains("Static data (ROM)         4 B", output, StringComparison.Ordinal);
    }
}
