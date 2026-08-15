using System.Globalization;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// One entry of the linker's <c>.sym</c>: a name, and where it ended up.
/// </summary>
/// <param name="Bank">The ROM bank, 0 for the resident one.</param>
/// <param name="Address">The address within the Game Boy's map, not an offset into the ROM.</param>
/// <param name="Name">The C name, without SDCC's leading underscore.</param>
public readonly record struct RomSymbol(int Bank, ushort Address, string Name);

/// <summary>
/// Reads which bank each symbol ended up in, from the linker's <c>.sym</c> file.
/// </summary>
/// <remarks>
/// <para>
/// This is how an automatic placement is answered. GB# hands bankpack a unit
/// marked "put this anywhere" and the packer decides; the decision has to come
/// back, or the layout is exactly the opaque thing banking is not allowed to be
/// (thesis section 15).
/// </para>
/// <para>
/// The <c>.sym</c> is used rather than bankpack's own output because bankpack
/// reports object files, and lcc compiles through temporary names, so by the time
/// it says "255 -&gt; 1" the file it names is <c>lcc12345.o</c>, which cannot be
/// traced back to a declaration. The <c>.sym</c> names symbols, which can.
/// </para>
/// <para>
/// The format is one line per symbol, <c>BB:AAAA _name</c>, with the bank in
/// hex and a leading underscore on the C name. It is already produced for every
/// build, because an emulator's debugger loads it.
/// </para>
/// </remarks>
public static class SymbolMapReader
{
    /// <summary>
    /// Maps C symbol names, without SDCC's leading underscore, to their bank.
    /// </summary>
    /// <returns>An empty map if the file is missing or unreadable.</returns>
    public static IReadOnlyDictionary<string, int> TryRead(string symbolPath)
    {
        var banks = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (RomSymbol symbol in TryReadSymbols(symbolPath))
        {
            // A symbol can appear more than once; the first placement wins,
            // and they agree in practice.
            banks.TryAdd(symbol.Name, symbol.Bank);
        }

        return banks;
    }

    /// <summary>
    /// Every symbol with its address, in the order the file lists them.
    /// </summary>
    /// <remarks>
    /// The address is what <see cref="TryRead"/> throws away, and it is what a
    /// running program counter has to be compared against: an address lands
    /// inside a function rather than on its first byte, so resolving one means
    /// searching for the nearest symbol at or below it. Duplicates are kept
    /// rather than collapsed, because dropping one would move that boundary.
    /// </remarks>
    /// <returns>An empty list if the file is missing or unreadable.</returns>
    public static IReadOnlyList<RomSymbol> TryReadSymbols(string symbolPath)
    {
        var symbols = new List<RomSymbol>();

        if (!File.Exists(symbolPath))
        {
            return symbols;
        }

        try
        {
            foreach (string line in File.ReadLines(symbolPath))
            {
                if (line.Length == 0 || line[0] == ';')
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                int space = line.IndexOf(' ');

                if (colon != 2 || space < 0 || space < colon)
                {
                    continue;
                }

                if (!int.TryParse(
                        line.AsSpan(0, colon),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out int bank))
                {
                    continue;
                }

                if (!ushort.TryParse(
                        line.AsSpan(colon + 1, space - colon - 1),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out ushort address))
                {
                    continue;
                }

                string name = line[(space + 1)..].Trim();
                if (name.StartsWith('_'))
                {
                    name = name[1..];
                }

                if (name.Length == 0)
                {
                    continue;
                }

                symbols.Add(new RomSymbol(bank, address, name));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A missing placement report never fails a build that linked.
            return symbols;
        }

        return symbols;
    }
}
