using System.Globalization;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// What one function cost while the profiler was gathering.
/// </summary>
/// <param name="Symbol">The C symbol the addresses belonged to.</param>
/// <param name="Method">The C# method it was lowered from, when there is one.</param>
/// <param name="File">The C# file holding that method.</param>
/// <param name="Line">The 1-based line of its declaration, or 0 when unknown.</param>
/// <param name="Count">Instructions executed inside it.</param>
/// <param name="Cycles">Ticks spent inside it, interrupts included.</param>
/// <param name="Invocations">Times it was entered from the top.</param>
public sealed record ProfileEntry(
    string Symbol,
    string? Method,
    string? File,
    int Line,
    long Count,
    long Cycles,
    long Invocations)
{
    /// <summary>The name to show a developer: their method if GB# wrote it, the symbol otherwise.</summary>
    public string DisplayName => Method ?? Symbol;

    /// <summary>Average ticks per call, or 0 when it was never entered from the top.</summary>
    public double CyclesPerInvocation => Invocations == 0 ? 0 : (double)Cycles / Invocations;
}

/// <summary>
/// Where a game's frame budget actually went, in the developer's own methods.
/// </summary>
/// <remarks>
/// <para>
/// The emulator counts instructions and ticks per ROM address. A ROM address
/// is the coordinate a linker map uses, so those counters and
/// <see cref="RomSymbolResolver"/> compose directly: totalling the counters
/// per symbol turns "address 0x4A21 cost 21,000 ticks" into "EnemySystem.Update
/// cost a third of your frame".
/// </para>
/// <para>
/// This is the answer GB# forked an emulator to be able to give. It is also the
/// measured counterpart of the cycle estimates the compiler reports at build
/// time: the estimate says what a method should cost, this says what it did.
/// </para>
/// <para>
/// Cycles rank code and counts explain it. A routine can be the most expensive
/// thing in a frame while being called twice, and ranking by count would bury
/// it under a loop of cheap loads.
/// </para>
/// </remarks>
public sealed class ProfileReport
{
    /// <summary>Ticks in one frame at 4194304Hz, which is the budget everything is measured against.</summary>
    public const int TicksPerFrame = 70224;

    private ProfileReport(IReadOnlyList<ProfileEntry> entries, long totalCycles, long unattributedCycles, int frames)
    {
        Entries = entries;
        TotalCycles = totalCycles;
        UnattributedCycles = unattributedCycles;
        Frames = frames;
    }

    /// <summary>Every function that ran, most expensive first.</summary>
    public IReadOnlyList<ProfileEntry> Entries { get; }

    /// <summary>Ticks accounted for across every address, attributed or not.</summary>
    public long TotalCycles { get; }

    /// <summary>
    /// Ticks at addresses no symbol covered.
    /// </summary>
    /// <remarks>
    /// Not swept under the rug. A large share here means the map and the ROM
    /// disagree, and a report that quietly renormalised would hide that by
    /// making the remaining percentages add up.
    /// </remarks>
    public long UnattributedCycles { get; }

    /// <summary>Frames the profile covers, or 0 when the caller did not say.</summary>
    public int Frames { get; }

    /// <summary>Ticks per frame this function cost, when the frame count is known.</summary>
    public double CyclesPerFrame(ProfileEntry entry) =>
        Frames <= 0 ? 0 : (double)entry.Cycles / Frames;

    /// <summary>That cost as a share of one frame's 70224 ticks.</summary>
    public double FrameBudgetShare(ProfileEntry entry) =>
        Frames <= 0 ? 0 : CyclesPerFrame(entry) / TicksPerFrame;

    /// <summary>
    /// Totals the emulator's per-address counters per symbol.
    /// </summary>
    /// <param name="resolver">Loaded from the ROM's own <c>.sym</c> and function map.</param>
    /// <param name="counts">Execution counts by ROM address, as the emulator reported them.</param>
    /// <param name="cycles">Ticks by ROM address, the same length.</param>
    /// <param name="frames">Frames the profile covers, for the frame-budget figures. 0 if unknown.</param>
    public static ProfileReport Build(
        RomSymbolResolver resolver,
        ReadOnlySpan<uint> counts,
        ReadOnlySpan<uint> cycles,
        int frames = 0)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var totals =
            new Dictionary<string, (CodeLocation Where, long Count, long Cycles, long Invocations)>(
                StringComparer.Ordinal);
        long total = 0;
        long unattributed = 0;

        int length = Math.Max(counts.Length, cycles.Length);
        for (int address = 0; address < length; address++)
        {
            long count = address < counts.Length ? counts[address] : 0;
            long cycle = address < cycles.Length ? cycles[address] : 0;

            // Only opcode addresses are ever counted, so most of a ROM is zero
            // and skipping it early is what keeps this a scan rather than a
            // symbol lookup per byte.
            if (count == 0 && cycle == 0)
            {
                continue;
            }

            total += cycle;

            if (Resolve(resolver, address) is not { } where)
            {
                unattributed += cycle;
                continue;
            }

            totals.TryGetValue(
                where.Symbol,
                out (CodeLocation Where, long Count, long Cycles, long Invocations) running);

            // The count at a symbol's own first address is how many times that
            // symbol was entered from the top, because its first instruction
            // runs exactly once per call. Code jumped into below its entry --
            // a tail call, or a loop re-entered by a jump -- is not counted,
            // which is the honest reading: it was not called.
            long invocations = where.Offset == 0 ? count : running.Invocations;

            totals[where.Symbol] =
                (where, running.Count + count, running.Cycles + cycle, invocations);
        }

        ProfileEntry[] entries =
        [
            .. totals.Values
                .Select(t => new ProfileEntry(
                    t.Where.Symbol, t.Where.Method, t.Where.File, t.Where.Line,
                    t.Count, t.Cycles, t.Invocations))
                .OrderByDescending(e => e.Cycles)
                .ThenByDescending(e => e.Count)
                .ThenBy(e => e.Symbol, StringComparer.Ordinal),
        ];

        return new ProfileReport(entries, total, unattributed, frames);
    }

    /// <summary>
    /// The top <paramref name="top"/> entries as a table, for a CLI to print.
    /// </summary>
    public string Describe(int top = 10)
    {
        if (Entries.Count == 0)
        {
            return "No code was profiled. The runtime must be the debug flavour and profiling must be on.";
        }

        var text = new System.Text.StringBuilder();
        text.Append(Frames > 0
            ? $"Frame budget over {Frames} frames, {TicksPerFrame} ticks each:"
            : "Ticks by function:");
        text.AppendLine();

        foreach (ProfileEntry entry in Entries.Take(top))
        {
            text.Append("  ");
            text.Append(entry.DisplayName.PadRight(40));

            text.Append(Frames > 0
                ? $"{FrameBudgetShare(entry) * 100,6:0.0}% of a frame  {CyclesPerFrame(entry),9:0} ticks/frame"
                : $"{entry.Cycles,12} ticks");

            text.Append(entry.Invocations > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {entry.Invocations,8} calls  {entry.CyclesPerInvocation,8:0} ticks each")
                : string.Create(CultureInfo.InvariantCulture, $"  {entry.Count,8} instrs{new string(' ', 20)}"));

            if (entry.File is not null && entry.Line > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {FunctionMapEntry.ShortFileName(entry.File)}:{entry.Line}");
            }

            text.AppendLine();
        }

        if (UnattributedCycles > 0)
        {
            double share = TotalCycles == 0 ? 0 : (double)UnattributedCycles / TotalCycles * 100;
            text.Append(CultureInfo.InvariantCulture,
                $"  {"(no symbol)",-40}{share,6:0.0}% of all ticks, at addresses the map does not cover");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns an offset into the cartridge into the bank and address the CPU
    /// saw, which is what the map is keyed by.
    /// </summary>
    /// <remarks>
    /// Bank 0 is mapped at 0x0000 and every other bank at 0x4000, so the two
    /// halves of a bank-sized window are not the same arithmetic.
    /// </remarks>
    private static CodeLocation? Resolve(RomSymbolResolver resolver, int romAddress)
    {
        const int BankSize = 0x4000;

        int bank = romAddress / BankSize;
        int offset = romAddress % BankSize;
        ushort address = (ushort)(bank == 0 ? offset : BankSize + offset);

        return resolver.Resolve(bank, address);
    }
}
