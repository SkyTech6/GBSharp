using System.Text.Json;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// What a ROM address turned out to be.
/// </summary>
/// <param name="Bank">The bank the address was resolved in.</param>
/// <param name="Address">The address asked about.</param>
/// <param name="Symbol">The nearest symbol at or below it.</param>
/// <param name="SymbolAddress">Where that symbol starts, so the offset into it is visible.</param>
/// <param name="Method">The C# method, when the symbol is a function GB# emitted from one.</param>
/// <param name="File">The C# file holding that method.</param>
/// <param name="Line">The 1-based line of its declaration, or 0 when unknown.</param>
public sealed record CodeLocation(
    int Bank,
    ushort Address,
    string Symbol,
    ushort SymbolAddress,
    string? Method,
    string? File,
    int Line)
{
    /// <summary>Bytes from the start of <see cref="Symbol"/> to <see cref="Address"/>.</summary>
    public int Offset => Address - SymbolAddress;

    /// <summary>
    /// The location as a person reads it: the C# method and line when GB#
    /// wrote the code, and the symbol otherwise.
    /// </summary>
    public override string ToString()
    {
        string where = $"{Bank:X2}:{Address:X4} {Symbol}" + (Offset == 0 ? string.Empty : $"+{Offset}");

        return Method is null
            ? where
            : $"{where} - {Method}" + (File is null ? string.Empty : $" ({FunctionMapEntry.ShortFileName(File)}:{Line})");
    }
}

/// <summary>
/// One symbol and the addresses it covers, joined to its C# origin.
/// </summary>
/// <param name="Bank">The ROM bank it lives in.</param>
/// <param name="Start">Its first address.</param>
/// <param name="End">One past its last, taken from where the next symbol begins.</param>
/// <param name="Symbol">The C name.</param>
/// <param name="Method">The C# method, when GB# emitted it from one.</param>
/// <param name="File">The C# file holding that method.</param>
/// <param name="Line">The 1-based line of its declaration, or 0.</param>
public sealed record SymbolExtent(
    int Bank,
    ushort Start,
    ushort End,
    string Symbol,
    string? Method,
    string? File,
    int Line)
{
    /// <summary>Addresses covered.</summary>
    public int Length => End - Start;

    /// <summary>
    /// The offset into the cartridge file where this symbol begins.
    /// </summary>
    /// <remarks>
    /// Bank 0 is mapped at 0x0000 and every other bank at 0x4000, so the two
    /// are not the same arithmetic. This is the coordinate the emulator's
    /// per-address arrays are indexed by.
    /// </remarks>
    public int RomAddress => (Bank * 0x4000) + (Bank == 0 ? Start : Start - 0x4000);
}

/// <summary>
/// Turns a running address back into the C# that produced it.
/// </summary>
/// <remarks>
/// <para>
/// The chain has three links and this is the one that joins them:
/// </para>
/// <code>
/// PC 0x4A21 + bank 3  ->  .sym  ->  EnemySystem_Update  ->  .functions.json  ->  EnemySystem.cs:42
/// </code>
/// <para>
/// The emulator supplies the first link, <c>ProgramCounter</c> and
/// <c>RomBankAt</c> on <c>GameBoy</c>, and it deliberately knows nothing about
/// either file. That is the same boundary the ABI draws: the emulator reports
/// where the CPU is, and GB# is what knows what that means.
/// </para>
/// <para>
/// Both files are produced by every build. Neither is required: a resolver
/// missing one still answers what the other can, so a partially cleaned build
/// directory degrades to a symbol name rather than to an exception.
/// </para>
/// </remarks>
public sealed class RomSymbolResolver
{
    /// <summary>Symbols of one bank, sorted by address, for a binary search.</summary>
    private readonly Dictionary<int, RomSymbol[]> _byBank;

    private readonly Dictionary<string, FunctionMapEntry> _functions;

    private RomSymbolResolver(
        Dictionary<int, RomSymbol[]> byBank,
        Dictionary<string, FunctionMapEntry> functions)
    {
        _byBank = byBank;
        _functions = functions;
    }

    /// <summary>True when there is nothing to resolve against.</summary>
    public bool IsEmpty => _byBank.Count == 0;

    /// <summary>
    /// Loads the two artefacts a build leaves beside its ROM.
    /// </summary>
    /// <remarks>
    /// Named after the ROM rather than taking two paths, because that is how
    /// the linker names its products and how <c>RomBuilder</c> writes them: a
    /// caller that has the ROM has both of these.
    /// </remarks>
    public static RomSymbolResolver ForRom(string romPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(romPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(romPath);

        return Load(
            Path.Combine(directory, stem + ".sym"),
            Path.Combine(directory, stem + ".functions.json"));
    }

    /// <summary>Loads from explicit paths. Either may be missing.</summary>
    public static RomSymbolResolver Load(string symbolPath, string functionMapPath)
    {
        var byBank = new Dictionary<int, RomSymbol[]>();

        foreach (IGrouping<int, RomSymbol> bank in SymbolMapReader.TryReadSymbols(symbolPath)
                     .GroupBy(symbol => symbol.Bank))
        {
            byBank[bank.Key] = [.. bank.OrderBy(symbol => symbol.Address)];
        }

        return new RomSymbolResolver(byBank, ReadFunctions(functionMapPath));
    }

    /// <summary>
    /// The code at <paramref name="address"/> in <paramref name="bank"/>, or
    /// <see langword="null"/> when nothing can be said about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A negative bank is the emulator's answer for an address outside the
    /// cartridge, which is a real case rather than an error (a bank-switching
    /// trampoline runs from HRAM), and there is nothing in a ROM map to resolve
    /// it against.
    /// </para>
    /// <para>
    /// An address lands inside a function, so the answer is the nearest symbol
    /// at or below it. Nothing in a <c>.sym</c> gives a symbol's length, so a
    /// stray address in padding between two functions resolves to the earlier
    /// one rather than to nothing; <see cref="CodeLocation.Offset"/> is what
    /// makes that visible.
    /// </para>
    /// </remarks>
    public CodeLocation? Resolve(int bank, ushort address)
    {
        if (bank < 0 || !_byBank.TryGetValue(bank, out RomSymbol[]? symbols))
        {
            return null;
        }

        int index = NearestAtOrBelow(symbols, address);
        if (index < 0)
        {
            return null;
        }

        RomSymbol symbol = symbols[index];

        // The nearest symbol below is only the right answer if it is in the
        // same region of the address map. The cartridge is two independently
        // banked 16KB windows, and the last symbol of the fixed one does not
        // run on into the switchable one: a program counter at 0x4121 is not
        // three kilobytes into main.
        if (Region(symbol.Address) != Region(address))
        {
            return null;
        }

        _functions.TryGetValue(symbol.Name, out FunctionMapEntry? function);

        return new CodeLocation(
            bank,
            address,
            symbol.Name,
            symbol.Address,
            function?.Method,
            function?.File,
            function?.Line ?? 0);
    }

    /// <summary>
    /// Every symbol in the cartridge with the addresses it covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.sym</c> records where each symbol starts and nothing about how
    /// long it is, so a symbol is taken to run until the next one in the same
    /// bank and region, or to the end of that region if it is the last. That is
    /// the same assumption <see cref="Resolve"/> makes, stated as a range
    /// rather than a search.
    /// </para>
    /// <para>
    /// Symbols above 0x7FFF are skipped: they are in RAM, which is not part of
    /// the cartridge and has no ROM address to be measured at.
    /// </para>
    /// </remarks>
    public IEnumerable<SymbolExtent> Extents()
    {
        foreach ((int bank, RomSymbol[] symbols) in _byBank)
        {
            for (int i = 0; i < symbols.Length; i++)
            {
                RomSymbol symbol = symbols[i];
                if (symbol.Address >= 0x8000)
                {
                    continue;
                }

                // The end of the region this symbol sits in: 0x4000 for the
                // fixed one, 0x8000 for the switchable one.
                int regionEnd = (Region(symbol.Address) + 1) << 14;

                int end = regionEnd;
                if (i + 1 < symbols.Length &&
                    Region(symbols[i + 1].Address) == Region(symbol.Address))
                {
                    end = symbols[i + 1].Address;
                }

                if (end <= symbol.Address)
                {
                    // Two symbols at one address, which a .sym does allow. The
                    // second covers nothing rather than covering backwards.
                    continue;
                }

                _functions.TryGetValue(symbol.Name, out FunctionMapEntry? function);

                yield return new SymbolExtent(
                    bank,
                    symbol.Address,
                    (ushort)end,
                    symbol.Name,
                    function?.Method,
                    function?.File,
                    function?.Line ?? 0);
            }
        }
    }

    /// <summary>Which 16KB window of the address map an address is in.</summary>
    private static int Region(ushort address) => address >> 14;

    private static int NearestAtOrBelow(RomSymbol[] symbols, ushort address)
    {
        int low = 0;
        int high = symbols.Length - 1;
        int found = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (symbols[middle].Address <= address)
            {
                // Keep going right: a later symbol may be closer and still below.
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private static Dictionary<string, FunctionMapEntry> ReadFunctions(string path)
    {
        var functions = new Dictionary<string, FunctionMapEntry>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return functions;
        }

        try
        {
            FunctionMapEntry[]? entries = JsonSerializer.Deserialize(
                File.ReadAllText(path), FunctionMapJson.Default.FunctionMapEntryArray);

            foreach (FunctionMapEntry entry in entries ?? [])
            {
                functions.TryAdd(entry.Name, entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Half a chain still names the symbol, which beats refusing to answer.
            return functions;
        }

        return functions;
    }
}
