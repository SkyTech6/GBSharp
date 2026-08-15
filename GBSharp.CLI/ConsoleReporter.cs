using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Cli.Reporting;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Cli;

/// <summary>
/// Renders diagnostics and build reports for the terminal.
/// </summary>
/// <remarks>
/// A build should say more than "compilation succeeded" (thesis section 16). A
/// developer working against 8 KB of WRAM and a 32 KB ROM needs to see what
/// they just spent, every time, without asking.
/// </remarks>
public static class ConsoleReporter
{
    private static readonly char[] NewLines = ['\n', '\r'];

    public static void WriteDiagnostics(IReadOnlyList<GBDiagnostic> diagnostics)
    {
        foreach (GBDiagnostic diagnostic in diagnostics.OrderBy(d => d.Span.FilePath, StringComparer.Ordinal)
                                                       .ThenBy(d => d.Span.Line))
        {
            WriteDiagnostic(diagnostic);
        }
    }

    public static void WriteDiagnostic(GBDiagnostic diagnostic)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ColorFor(diagnostic.Severity);

        string location = diagnostic.Span.IsNone
            ? string.Empty
            : $"{ShortPath(diagnostic.Span.FilePath)}({diagnostic.Span.Line},{diagnostic.Span.Column}): ";

        Console.WriteLine($"{location}{Label(diagnostic.Severity)} {diagnostic.Id}: {diagnostic.Message}");
        Console.ForegroundColor = previous;

        WriteSourceExcerpt(diagnostic.Span);

        if (diagnostic.Descriptor.Help is { Length: > 0 } help)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    {help}");
            Console.ForegroundColor = previous;
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Shows the offending C# line with a caret. GB# points at C# source, never
    /// at generated C (thesis section 20).
    /// </summary>
    private static void WriteSourceExcerpt(SourceSpan span)
    {
        if (span.IsNone || !File.Exists(span.FilePath))
        {
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllText(span.FilePath).Split(NewLines, StringSplitOptions.None);
        }
        catch (IOException)
        {
            return;
        }

        if (span.Line < 1 || span.Line > lines.Length)
        {
            return;
        }

        string text = lines[span.Line - 1].Replace("\t", "    ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    {text}");

        int caret = Math.Max(0, span.Column - 1);
        int width = Math.Max(1, Math.Min(span.Length, Math.Max(1, text.Length - caret)));
        Console.WriteLine($"    {new string(' ', caret)}{new string('^', width)}");
        Console.ResetColor();
    }

    /// <summary>
    /// Prints the build report.
    /// </summary>
    /// <remarks>
    /// Takes the assembled <see cref="BuildReport"/> rather than recomputing its
    /// numbers, so the terminal and <c>--report-json</c> cannot disagree, which
    /// is what that record's remarks have always claimed and what, until this
    /// took its figures from there, was not actually true. The module is still
    /// needed for the parts the report does not carry, such as which declaration
    /// landed in which bank.
    /// </remarks>
    public static void WriteBuildReport(
        BuildReport report,
        IRModule module,
        string romPath,
        GBSharp.Backend.GBDK.GBTarget target,
        IReadOnlyList<GBDiagnostic> diagnostics,
        RomUsageReport? usage = null)
    {
        long romBytes = report.RomBytes;

        // Read-only data is const in the generated C and lives in the cartridge.
        // Summing it into the WRAM figure would report a tileset as if it were
        // eating the 8 KB of work RAM, which is the opposite of the truth.
        int declaredWram = report.Memory.DeclaredWram;
        int declaredRomData = report.Memory.DeclaredRom;

        int warnings = diagnostics.Count(d => d.Severity == GBSeverity.Warning);
        int performance = diagnostics.Count(d => d.Severity == GBSeverity.Performance);
        int resource = diagnostics.Count(d => d.Severity == GBSeverity.Resource);

        Console.WriteLine("GB# Build Report");
        Console.WriteLine("────────────────────────────────");
        Console.WriteLine();
        WriteRow("Target", target == GBSharp.Backend.GBDK.GBTarget.GameBoyColor ? "Game Boy Color" : "Game Boy");
        WriteRow("ROM", FormatBytes(romBytes));

        if (module.Functions.Any(f => !f.Bank.IsResident) || module.Globals.Any(g => !g.Bank.IsResident))
        {
            WriteRow("Cartridge", DescribeCartridge(romPath));
        }

        // Two different WRAM numbers exist and they are not the same thing: what
        // the developer declared, and what the linker placed. The second is
        // larger, because it includes the stack, shadow OAM and library state.
        // Showing only the first would understate the real budget.
        if (usage is not null)
        {
            WriteRow("WRAM used", $"{FormatBytes(usage.WramUsed)} / {FormatBytes(usage.WramSize)}");
        }

        if (declaredRomData > 0)
        {
            WriteRow("Static data (ROM)", FormatBytes(declaredRomData));
        }

        WriteRow("Static objects (declared)", FormatBytes(declaredWram));
        WriteRow("Structs", module.Structs.Count.ToString());
        WriteRow("Functions", module.Functions.Count.ToString());

        WriteAssetReport(module);

        if (usage is not null && usage.Rom.Any())
        {
            // What GB# placed in each bank, to sit beside what the linker did.
            // The two differ by the code in that bank, which is the difference
            // between "my data fits" and "this bank fits".
            var declaredByBank = module.Globals
                .Where(g => g.Bank.Kind == IRBankKind.Fixed)
                .GroupBy(g => g.Bank.Number)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Type.SizeInBytes));

            Console.WriteLine();
            Console.WriteLine("ROM Banks");

            foreach (BankUsage bank in usage.Rom.OrderBy(b => b.BankNumber))
            {
                string label = $"  Bank {bank.BankNumber}";
                string value = $"{FormatBytes(bank.Used)} / {FormatBytes(bank.Size)}  {Meter(bank.UsedPercent)}";

                string declared = declaredByBank.TryGetValue(bank.BankNumber, out int bytes)
                    ? $"  ({FormatBytes(bytes)} declared)"
                    : string.Empty;

                Console.WriteLine($"{label.PadRight(26)}{value}{declared}");
            }

            WritePlacement(module);
        }

        WriteCycles(report.Cycles);
        WriteCallStack(report.Stack, usage);
        WriteBudgets(module, romPath, usage);

        Console.WriteLine();
        WriteRow("Warnings", warnings.ToString());
        WriteRow("Performance warnings", performance.ToString());
        WriteRow("Resource notes", resource.ToString());
        Console.WriteLine();
        Console.WriteLine($"Output: {romPath}");
    }

    /// <summary>
    /// Estimated cycle costs, against the frame they have to fit in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skipped entirely when there is nothing to say, so a small project's report
    /// is byte-for-byte what it was before this section existed.
    /// </para>
    /// <para>
    /// The caveat line is not decoration. It is the reason this section is
    /// allowed to print numbers at all, and it prints once, here, rather than
    /// being repeated into every row.
    /// </para>
    /// </remarks>
    private static void WriteCycles(CyclesInfo? cycles)
    {
        if (cycles is null || (cycles.FrameLoopCycles is null && cycles.Functions.Count == 0))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Cycle estimates");

        // Printed exactly, not rounded. This is the one figure in the section
        // that is a property of the hardware rather than an estimate, and it is
        // what gives every other number here a denominator worth quoting.
        WriteRow(
            "  Frame budget",
            $"{cycles.FrameCycles:N0} cycles @ {Sm83CostTable.FramesPerSecond:F1} Hz");

        if (cycles.FrameLoopCycles is { } loop && cycles.FrameLoopPercent is { } percent)
        {
            WriteRow("  Frame loop", $"{Cycles(loop)} cycles  {Meter(percent)}  {percent}%");
        }

        foreach (FunctionCostInfo function in cycles.Functions)
        {
            WriteRow($"  {function.Name}", $"{Cycles(function.Cycles)} cycles{(function.Partial ? "  (partial)" : "")}");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Estimated statically from the IR. GB# does not model SDCC's register");
        Console.WriteLine("  allocator, so read these as ceilings for comparing changes, not as");
        Console.WriteLine("  measurements.");
        Console.ResetColor();
    }

    /// <summary>
    /// How deep the calls go, beside what the linker actually placed.
    /// </summary>
    /// <remarks>
    /// The depth is exact and the byte figure is measured; neither is an
    /// estimate, which is why this is a section of its own rather than a row
    /// under the caveat above.
    /// </remarks>
    private static void WriteCallStack(StackInfo? stack, RomUsageReport? usage)
    {
        if (stack is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Call stack");

        if (stack.Bounded)
        {
            WriteRow("  Deepest path", $"{stack.Calls} calls   {string.Join(" -> ", stack.DeepestPath)}");
        }
        else
        {
            // A recursive program has no maximum. GBS0058 has said why; printing
            // the acyclic depth here would undo that by offering a number.
            WriteRow("  Deepest path", "unbounded (recursive)");
        }

        if (usage is not null)
        {
            WriteRow("  Work RAM free", $"{FormatBytes(usage.WramSize - usage.WramUsed)} for stack and locals");
        }
    }

    private static string Cycles(int value) =>
        Sm83CostTable.RoundForDisplay(value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Declared budgets against what the build actually used.
    /// </summary>
    /// <remarks>
    /// Only shown when a budget was declared. Printed even when it passed,
    /// because the value of a budget is partly in watching the headroom shrink.
    /// </remarks>
    private static void WriteBudgets(IRModule module, string? romPath, RomUsageReport? usage)
    {
        if (!module.Budgets.Any)
        {
            return;
        }

        RomHeader? header = romPath is null ? null : RomHeader.Read(romPath);

        Console.WriteLine();
        Console.WriteLine("Budgets");

        if (module.Budgets.MaxWram is { } wram)
        {
            WriteBudget("WRAM", usage?.WramUsed, wram);
        }

        if (module.Budgets.MaxRom is { } rom)
        {
            WriteBudget("ROM", header?.SizeInBytes, rom);
        }

        if (module.Budgets.MaxRomBanks is { } banks)
        {
            WriteBudget("ROM banks", header?.DeclaredRomBanks, banks, formatAsBytes: false);
        }
    }

    private static void WriteBudget(string label, int? actual, int limit, bool formatAsBytes = true)
    {
        string Format(int value) => formatAsBytes ? FormatBytes(value) : value.ToString();

        string value = actual is null
            ? $"? / {Format(limit)}"
            : $"{Format(actual.Value)} / {Format(limit)}";

        bool exceeded = actual is { } a && a > limit;

        ConsoleColor? previous = null;
        if (exceeded)
        {
            previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
        }

        Console.WriteLine($"  {label.PadRight(24)}{value}{(exceeded ? "  EXCEEDED" : string.Empty)}");

        if (previous is not null)
        {
            Console.ForegroundColor = previous.Value;
        }
    }

    /// <summary>
    /// What each converted asset cost.
    /// </summary>
    /// <remarks>
    /// Shared by the build report and by <c>gbsharp assets</c>, which needs no
    /// toolchain. One renderer, so the numbers an artist sees during a fast loop
    /// are the numbers the build reports.
    /// </remarks>
    public static void WriteAssetReport(IRModule module)
    {
        if (module.Assets.Count == 0)
        {
            return;
        }

        bool anyBanked = module.Assets.Any(a => !a.Bank.IsResident);

        Console.WriteLine();
        Console.WriteLine("Assets");

        foreach (IRAsset asset in module.Assets)
        {
            string tiles = asset.Stats.TilesSavedByFlip > 0
                ? $"{asset.Stats.TotalTiles} -> {asset.Stats.UniqueTiles} tiles ({asset.Stats.TilesSavedByFlip} by flip)"
                : $"{asset.Stats.TotalTiles} -> {asset.Stats.UniqueTiles} tiles";

            string shape = $"{asset.Stats.WidthTiles}x{asset.Stats.HeightTiles}";
            string palettes = asset.Stats.PaletteCount > 0 ? $", {asset.Stats.PaletteCount} palettes" : string.Empty;

            // The bank column only appears once something is banked, so an
            // unbanked project's report is unchanged.
            string bank = anyBanked
                ? ("  bank " + (asset.Bank.IsResident ? "0" : asset.Bank.ToString())).PadRight(12)
                : string.Empty;

            Console.WriteLine(
                $"  {ShortName(asset.Name).PadRight(16)}{asset.SourceFile.PadRight(16)}" +
                $"{shape.PadRight(8)}{tiles}{palettes}".PadRight(44) +
                FormatBytes(asset.RomBytes) +
                bank);
        }
    }

    /// <summary>
    /// The mapper and bank count, read back from the ROM that was produced.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than from the build options, so the report
    /// describes the cartridge that exists rather than the one that was asked
    /// for. Those differ when the linker sizes the image itself.
    /// </remarks>
    private static string DescribeCartridge(string? romPath)
    {
        if (romPath is null || RomHeader.Read(romPath) is not { } header)
        {
            return "unknown";
        }

        return header.HasMbc
            ? $"MBC 0x{header.CartridgeType:X2}, {header.DeclaredRomBanks} banks"
            : "no mapper";
    }

    /// <summary>
    /// Lists which declarations landed in which bank.
    /// </summary>
    /// <remarks>
    /// Only shown once something is banked, so an unbanked build's report is
    /// unchanged. This is the answer to "why is bank 1 full?", and the reason
    /// automatic placement is allowed to exist at all: the layout may be chosen
    /// for you, but it is never unavailable (thesis section 15).
    /// </remarks>
    private static void WritePlacement(IRModule module)
    {
        var banked = module.Functions
            .Where(f => !f.Bank.IsResident)
            .Select(f => (f.Bank, Name: f.SourceName ?? f.Name))
            .Concat(module.Globals
                .Where(g => !g.Bank.IsResident)
                .Select(g => (g.Bank, Name: g.Name)))
            .GroupBy(x => x.Bank)
            .OrderBy(g => g.Key.Kind == IRBankKind.Automatic)
            .ThenBy(g => g.Key.Number)
            .ToList();

        if (banked.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Placement");

        foreach (var group in banked)
        {
            string label = group.Key.Kind == IRBankKind.Automatic
                ? "  Bank (chosen)"
                : $"  Bank {group.Key.Number}";

            // Full names, not shortened: this list answers "what is in this
            // bank", and 'Load()' without its class does not answer it.
            Console.WriteLine($"{label.PadRight(26)}{string.Join(", ", group.Select(x => x.Name).Order(StringComparer.Ordinal))}");
        }
    }

    /// <summary>
    /// One label/value row in the report's two columns.
    /// </summary>
    /// <remarks>
    /// A label longer than the column still gets a gap rather than running into
    /// its value. Generated names can be long, and "capacity 81,200 cycles" reads
    /// as a number that is not there.
    /// </remarks>
    private static void WriteRow(string label, string value) =>
        Console.WriteLine(label.Length >= LabelColumn ? $"{label}  {value}" : $"{label.PadRight(LabelColumn)}{value}");

    private const int LabelColumn = 26;

    /// <summary>The field name without its containing type, for a narrow column.</summary>
    private static string ShortName(string name) =>
        name.LastIndexOf('.') is var dot && dot >= 0 ? name[(dot + 1)..] : name;

    /// <summary>A 20-cell bar, so bank pressure reads at a glance.</summary>
    private static string Meter(int percent)
    {
        int filled = Math.Clamp(percent * 20 / 100, 0, 20);
        return new string('█', filled) + new string('░', 20 - filled);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024
            ? $"{bytes / 1024.0:0.0} KB"
            : $"{bytes} B";

    private static string ShortPath(string path)
    {
        try
        {
            string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
            return relative.Length < path.Length ? relative : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string Label(GBSeverity severity) => severity switch
    {
        GBSeverity.Error => "error",
        GBSeverity.Warning => "warning",
        GBSeverity.Performance => "performance",
        GBSeverity.Resource => "resource",
        _ => "info",
    };

    private static ConsoleColor ColorFor(GBSeverity severity) => severity switch
    {
        GBSeverity.Error => ConsoleColor.Red,
        GBSeverity.Warning => ConsoleColor.Yellow,
        GBSeverity.Performance => ConsoleColor.Magenta,
        GBSeverity.Resource => ConsoleColor.Cyan,
        _ => ConsoleColor.Gray,
    };
}
