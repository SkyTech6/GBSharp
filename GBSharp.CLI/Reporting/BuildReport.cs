using System.Text.Json.Serialization;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Cli.Reporting;

/// <summary>
/// Everything a build produced, in a form both a terminal and a machine can read.
/// </summary>
/// <remarks>
/// <para>
/// The console output was previously computed inline while printing, which made
/// it the only representation and untestable except by matching strings. Building
/// this first means the JSON and the terminal cannot disagree, and a test can
/// assert on numbers instead of on formatting.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> is here from the start because the consumer is a
/// CI script somebody else wrote.
/// </para>
/// </remarks>
public sealed record BuildReport(
    int SchemaVersion,
    ToolchainInfo Toolchain,
    string Target,
    string? RomPath,
    long RomBytes,
    string Cartridge,
    MemoryInfo Memory,
    IReadOnlyList<GlobalInfo> Globals,
    IReadOnlyList<AssetInfo> Assets,
    IReadOnlyList<BankInfo> Banks,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    IReadOnlyList<BudgetInfo> Budgets,
    CyclesInfo? Cycles,
    StackInfo? Stack)
{
    /// <summary>
    /// Bumped only when a consumer would have to change.
    /// </summary>
    /// <remarks>
    /// The cycle and stack sections did not bump it. They are additive and
    /// nullable, the serializer drops nulls, and a script written against version
    /// 1 reads exactly what it did before.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    public static BuildReport Create(
        IRModule module,
        string? romPath,
        GBTarget target,
        IReadOnlyList<GBDiagnostic> diagnostics,
        RomUsageReport? usage,
        string? gbdkVersion)
    {
        RomHeader? header = romPath is null ? null : RomHeader.Read(romPath);

        // Declared and actual are different numbers and always reported apart.
        // Summing read-only data into the WRAM figure would report a tileset as
        // if it were eating the 8 KB of work RAM.
        //
        // Both totals are read off the module rather than summed here. They used
        // to be computed independently in this method and again in the console
        // reporter, which is exactly the drift this record's remarks claim it
        // prevents; one place to compute them is the fix.
        int declaredWram = module.Costs.StaticWramBytes;
        int declaredRom = module.Costs.StaticRomBytes;

        return new BuildReport(
            CurrentSchemaVersion,
            new ToolchainInfo(
                typeof(BuildReport).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                gbdkVersion,
                Environment.Version.ToString()),
            target == GBTarget.GameBoyColor ? "gbc" : "gb",
            romPath,
            romPath is not null && File.Exists(romPath) ? new FileInfo(romPath).Length : 0,
            header is null ? "unknown" : header.HasMbc ? $"MBC 0x{header.CartridgeType:X2}" : "none",
            new MemoryInfo(
                declaredWram,
                declaredRom,
                usage?.WramUsed,
                usage?.WramSize),
            [
                .. module.Globals
                    .OrderByDescending(g => g.Type.SizeInBytes)
                    .Select(g => new GlobalInfo(
                        g.Name,
                        g.Type.SizeInBytes,
                        g.IsReadOnly ? "rom" : "wram",
                        g.Bank.IsResident ? 0 : g.Bank.Number,
                        g.Span.IsNone ? null : g.Span.ToString())),
            ],
            [
                .. module.Assets.Select(a => new AssetInfo(
                    a.Name,
                    a.SourceFile,
                    a.Stats.WidthTiles,
                    a.Stats.HeightTiles,
                    a.Stats.TotalTiles,
                    a.Stats.UniqueTiles,
                    a.Stats.PaletteCount,
                    a.RomBytes,
                    a.Bank.IsResident ? 0 : a.Bank.Number)),
            ],
            [
                .. (usage?.Rom ?? []).OrderBy(b => b.BankNumber).Select(b => new BankInfo(
                    b.BankNumber,
                    b.Used,
                    b.Size,
                    module.Globals
                        .Where(g => g.Bank.Kind == IRBankKind.Fixed && g.Bank.Number == b.BankNumber)
                        .Sum(g => g.Type.SizeInBytes))),
            ],
            [
                .. diagnostics.Select(d => new DiagnosticInfo(
                    d.Id,
                    d.Severity.ToString().ToLowerInvariant(),
                    d.Message,
                    d.Span.IsNone ? null : d.Span.FilePath,
                    d.Span.IsNone ? null : d.Span.Line)),
            ],
            DescribeBudgets(module, header, usage),
            DescribeCycles(module),
            DescribeStack(module));
    }

    /// <summary>
    /// The cost estimates, or null when there was nothing worth estimating.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty object, so a consumer can tell "GB# had nothing
    /// to say" from "GB# said zero".
    /// </remarks>
    private static CyclesInfo? DescribeCycles(IRModule module)
    {
        ModuleCostReport costs = module.Costs;

        if (costs.IsEmpty)
        {
            return null;
        }

        return new CyclesInfo(
            Sm83CostTable.FrameCycles,
            costs.FrameLoop?.PerIterationCycles,
            costs.FrameLoop is { } loop ? Sm83CostTable.PercentOfFrame(loop.PerIterationCycles) : null,
            [
                .. costs.Functions
                    .Where(f => f.Cycles > 0 && !f.IsCompilerGenerated)
                    .Take(TopFunctions)
                    .Select(f => new FunctionCostInfo(f.DisplayName, f.Cycles, f.IsPartial)),
            ]);
    }

    private static StackInfo? DescribeStack(IRModule module)
    {
        StackDepth stack = module.Costs.Stack;

        return stack.Calls == 0 ? null : new StackInfo(stack.Calls, stack.Bounded, stack.DeepestPath);
    }

    /// <summary>
    /// How many functions the ranking carries.
    /// </summary>
    /// <remarks>
    /// A ranking is for finding where the time went, and past the first handful
    /// it stops answering that. Reported rather than silently truncated: the
    /// count is part of the shape a consumer reads.
    /// </remarks>
    public const int TopFunctions = 5;

    private static IReadOnlyList<BudgetInfo> DescribeBudgets(
        IRModule module,
        RomHeader? header,
        RomUsageReport? usage)
    {
        var budgets = new List<BudgetInfo>();

        if (module.Budgets.MaxWram is { } wram)
        {
            budgets.Add(new BudgetInfo("wram", usage?.WramUsed, wram));
        }

        if (module.Budgets.MaxRom is { } rom)
        {
            budgets.Add(new BudgetInfo("rom", header?.SizeInBytes, rom));
        }

        if (module.Budgets.MaxRomBanks is { } banks)
        {
            budgets.Add(new BudgetInfo("romBanks", header?.DeclaredRomBanks, banks));
        }

        return budgets;
    }
}

/// <summary>What produced this ROM. Thesis section 21: pin or report.</summary>
public sealed record ToolchainInfo(string GBSharp, string? Gbdk, string DotnetRuntime);

/// <param name="DeclaredWram">Bytes of mutable static data the source declared.</param>
/// <param name="DeclaredRom">Bytes of read-only static data the source declared.</param>
/// <param name="ActualWramUsed">
/// What the linker placed, including the stack, shadow OAM and library state.
/// Always larger than declared, and the number that matters.
/// </param>
public sealed record MemoryInfo(int DeclaredWram, int DeclaredRom, int? ActualWramUsed, int? WramSize);

public sealed record GlobalInfo(string Name, int Bytes, string Region, int Bank, string? Declared);

public sealed record AssetInfo(
    string Name,
    string Source,
    int WidthTiles,
    int HeightTiles,
    int TotalTiles,
    int UniqueTiles,
    int Palettes,
    int RomBytes,
    int Bank);

/// <param name="DeclaredBytes">What GB# placed here, against what the linker did.</param>
public sealed record BankInfo(int Bank, int Used, int Size, int DeclaredBytes);

public sealed record DiagnosticInfo(string Id, string Severity, string Message, string? File, int? Line);

/// <param name="Actual">Null when the build could not measure it.</param>
public sealed record BudgetInfo(string Resource, int? Actual, int Limit);

/// <summary>
/// Estimated cycle costs.
/// </summary>
/// <param name="FrameCycles">
/// T-cycles between frames. Hardware, and the only exact figure here.
/// </param>
/// <param name="FrameLoopCycles">
/// One iteration of the frame loop, or null if the program has no frame loop.
/// </param>
/// <param name="FrameLoopPercent">That iteration as a share of a frame.</param>
/// <param name="Functions">The dearest functions, most expensive first.</param>
/// <remarks>
/// Every figure but <paramref name="FrameCycles"/> is a static estimate from the
/// IR: GB# emits C and SDCC decides what runs, so these carry a wide error bar
/// and are useful comparatively rather than absolutely. A CI script should watch
/// them for change rather than assert a threshold on one build.
/// </remarks>
public sealed record CyclesInfo(
    int FrameCycles,
    int? FrameLoopCycles,
    int? FrameLoopPercent,
    IReadOnlyList<FunctionCostInfo> Functions);

/// <param name="Partial">
/// True if the estimate excludes a copy whose length is only known at runtime.
/// </param>
public sealed record FunctionCostInfo(string Name, int Cycles, bool Partial);

/// <param name="Calls">Frames on the deepest call path.</param>
/// <param name="Bounded">
/// False if the program can recurse, in which case <paramref name="Calls"/> is
/// not a maximum and must not be read as one.
/// </param>
/// <remarks>
/// Calls rather than bytes, and exact rather than estimated: GB# has no
/// delegates and no function pointers, so the call graph is the whole account of
/// what can reach what. This is the figure worth watching for growth in CI.
/// </remarks>
public sealed record StackInfo(int Calls, bool Bounded, IReadOnlyList<string> DeepestPath);

/// <summary>
/// Source-generated serialisation, so no reflection and no new package.
/// </summary>
/// <remarks>
/// Public alongside the record it serialises, so a test can round-trip a report
/// through the same context the CLI writes with. Asserting on JSON produced by a
/// different serializer would be asserting on something no consumer ever reads.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildReport))]
public sealed partial class BuildReportJson : JsonSerializerContext
{
}
