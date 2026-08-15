namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// How much of one function a play session actually reached.
/// </summary>
/// <param name="Symbol">The C symbol.</param>
/// <param name="Method">The C# method it was lowered from, when there is one.</param>
/// <param name="File">The C# file holding that method.</param>
/// <param name="Line">The 1-based line of its declaration, or 0.</param>
/// <param name="Bytes">Bytes the symbol covers.</param>
/// <param name="ExecutedBytes">How many of them were executed.</param>
/// <param name="Instructions">Distinct instructions in it that ran.</param>
public sealed record CoverageEntry(
    string Symbol,
    string? Method,
    string? File,
    int Line,
    int Bytes,
    int ExecutedBytes,
    int Instructions)
{
    /// <summary>The name to show a developer.</summary>
    public string DisplayName => Method ?? Symbol;

    /// <summary>Nothing in it ran.</summary>
    public bool Unreached => ExecutedBytes == 0;

    /// <summary>
    /// Executed bytes as a fraction of its size.
    /// </summary>
    /// <remarks>
    /// Sound for every symbol but the last in a bank, whose size includes the
    /// bank's trailing padding because nothing records where it really ends.
    /// <see cref="Unreached"/> is unaffected either way: no byte executed means
    /// no byte executed, however long the symbol is thought to be.
    /// </remarks>
    public double Share => Bytes == 0 ? 0 : (double)ExecutedBytes / Bytes;
}

/// <summary>
/// Which of a ROM's code a session never ran.
/// </summary>
/// <remarks>
/// <para>
/// The emulator marks every cartridge byte it reaches, as code or as data. The
/// flags are not the interesting part: the bytes carrying <em>no</em> flag are,
/// because those are the ones nothing ever touched. Totalled per symbol and
/// joined through the same map the profiler uses, that becomes a list of the
/// developer's own methods that a play session never entered.
/// </para>
/// <para>
/// This is coverage of one run, not of a test suite, and it says nothing about
/// correctness. What it is good for is the two questions a ROM budget makes
/// urgent: which code is dead and could be deleted, and which code a session
/// never exercised and so proved nothing about.
/// </para>
/// <para>
/// Takes raw bytes rather than the emulator's own enum so that this project
/// does not depend on <c>GBSharp.Emulator</c>. The flags are the ABI's, and
/// they are stable.
/// </para>
/// </remarks>
public sealed class CoverageReport
{
    /// <summary>Executed, or part of an instruction that was.</summary>
    private const byte Code = 0x1;

    /// <summary>The first byte of an executed instruction.</summary>
    private const byte CodeStart = 0x4;

    private CoverageReport(IReadOnlyList<CoverageEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>Every symbol in the cartridge, least covered first.</summary>
    public IReadOnlyList<CoverageEntry> Entries { get; }

    /// <summary>Symbols nothing reached.</summary>
    public IEnumerable<CoverageEntry> Unreached => Entries.Where(e => e.Unreached);

    /// <summary>Bytes of code that ran, across every symbol.</summary>
    public int ExecutedBytes => Entries.Sum(e => e.ExecutedBytes);

    /// <summary>
    /// Bytes every symbol covers, which is not the same as bytes of code.
    /// </summary>
    /// <remarks>
    /// A <c>.sym</c> gives no symbol its length, so the last symbol in each
    /// bank is charged with every byte from where it starts to the end of the
    /// bank, padding included. On a small ROM that is most of the bank, which
    /// is why this class reports no overall coverage percentage: the figure
    /// would be dominated by empty space and would improve by adding code.
    /// </remarks>
    public int TotalBytes => Entries.Sum(e => e.Bytes);

    /// <summary>
    /// Totals the emulator's per-byte usage flags per symbol.
    /// </summary>
    /// <param name="resolver">Loaded from the ROM's own <c>.sym</c> and function map.</param>
    /// <param name="usage">Usage flags by ROM address, as the emulator reported them.</param>
    public static CoverageReport Build(RomSymbolResolver resolver, ReadOnlySpan<byte> usage)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var entries = new List<CoverageEntry>();

        foreach (SymbolExtent extent in resolver.Extents())
        {
            int executed = 0;
            int instructions = 0;

            for (int offset = 0; offset < extent.Length; offset++)
            {
                int address = extent.RomAddress + offset;
                if (address >= usage.Length)
                {
                    break;
                }

                if ((usage[address] & Code) != 0)
                {
                    executed++;
                }

                // Counting opcode starts counts instructions rather than bytes,
                // which is the fairer measure: a three-byte instruction is no
                // more executed than a one-byte one.
                if ((usage[address] & CodeStart) != 0)
                {
                    instructions++;
                }
            }

            entries.Add(new CoverageEntry(
                extent.Symbol, extent.Method, extent.File, extent.Line,
                extent.Length, executed, instructions));
        }

        return new CoverageReport(
        [
            .. entries
                .OrderBy(e => e.Share)
                .ThenByDescending(e => e.Bytes)
                .ThenBy(e => e.Symbol, StringComparer.Ordinal),
        ]);
    }

    /// <summary>
    /// The methods a session never entered, for a CLI to print.
    /// </summary>
    /// <remarks>
    /// Only the ones GB# emitted from C#. A developer can act on their own dead
    /// method; they cannot act on an unreached branch of GBDK's runtime, and
    /// listing dozens of those would bury the rows they can do something about.
    /// </remarks>
    public string Describe(int top = 15)
    {
        if (Entries.Count == 0)
        {
            return "No coverage was gathered. The runtime must be the debug flavour.";
        }

        var text = new System.Text.StringBuilder();

        int reachedSymbols = Entries.Count(e => !e.Unreached);

        // Symbols reached, not a percentage of the ROM. A symbol's size is
        // inferred from where the next one starts, so the last in each bank
        // absorbs the bank's padding and any byte-based percentage would say
        // more about how empty the ROM is than about what ran.
        text.AppendLine(
            $"Reached {reachedSymbols} of {Entries.Count} symbols, {ExecutedBytes} bytes of code.");

        CoverageEntry[] unreached = [.. Unreached.Where(e => e.Method is not null)];

        if (unreached.Length == 0)
        {
            text.AppendLine("Every method GB# emitted was entered at least once.");
            return text.ToString();
        }

        text.AppendLine();
        text.AppendLine($"Methods never entered ({unreached.Length}):");

        foreach (CoverageEntry entry in unreached.Take(top))
        {
            text.Append("  ");
            text.Append(entry.DisplayName.PadRight(44));
            text.Append($"{entry.Bytes,6} bytes");

            if (entry.File is not null && entry.Line > 0)
            {
                text.Append($"  {FunctionMapEntry.ShortFileName(entry.File)}:{entry.Line}");
            }

            text.AppendLine();
        }

        if (unreached.Length > top)
        {
            text.AppendLine($"  ... and {unreached.Length - top} more");
        }

        return text.ToString();
    }
}
