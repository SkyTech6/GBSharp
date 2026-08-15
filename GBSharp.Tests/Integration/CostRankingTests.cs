using System.Globalization;
using GBSharp.Backend.GBDK;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Integration;

/// <summary>
/// Keeps the cost model roughly honest against what SDCC actually emits.
/// </summary>
/// <remarks>
/// <para>
/// The model estimates from the IR and never sees SDCC's output, so nothing in
/// the unit tests can catch it being wrong by a large factor: they assert the
/// model is self-consistent, not that it resembles the machine. This is the test
/// that would notice, and it needs the real toolchain, so it skips itself
/// without one the same way the other integration tests do.
/// </para>
/// <para>
/// It compares <em>ranks</em>, not values. Bytes are not cycles and never will
/// be; what a rank check catches is the failure that matters, which is the model
/// deciding some construct is cheap when SDCC makes it expensive.
/// </para>
/// <para>
/// The comparison is restricted to straight-line functions on purpose. A loop's
/// code size does not grow with its trip count: a loop of a hundred iterations
/// assembles to the same bytes as a loop of three, so for anything containing a
/// loop the two quantities are not even monotonically related, and a rank check
/// over them would be measuring nothing. Straight-line code is where more work
/// genuinely does mean more instructions.
/// </para>
/// </remarks>
public sealed class CostRankingTests
{
    /// <summary>
    /// Three functions of deliberately different weight, all straight-line.
    /// </summary>
    private const string Source = """
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            public static void Main()
            {
                Work.Tiny();
                Work.Medium();
                Work.Large();
            }
        }

        public static class Work
        {
            public static void Tiny()
            {
                Sprites[0].X = 1;
            }

            public static void Medium()
            {
                Sprites[0].X = 1;
                Sprites[0].Y = 2;
                Sprites[1].X = 3;
                Sprites[1].Y = 4;
                Sprites[2].X = 5;
                Sprites[2].Y = 6;
            }

            public static void Large()
            {
                Sprites[0].X = 1;
                Sprites[0].Y = 2;
                Sprites[1].X = 3;
                Sprites[1].Y = 4;
                Sprites[2].X = 5;
                Sprites[2].Y = 6;
                Sprites[3].X = 7;
                Sprites[3].Y = 8;
                Sprites[4].X = 9;
                Sprites[4].Y = 10;
                Sprites[5].X = 11;
                Sprites[5].Y = 12;
                Sprites[6].X = 13;
                Sprites[6].Y = 14;
                Sprites[7].X = 15;
                Sprites[7].Y = 16;
                Sprites[8].X = 17;
                Sprites[8].Y = 18;
            }
        }
        """;

    [Fact]
    public void PredictedCostRanksTheSameWayAsEmittedCode()
    {
        if (!TestHarness.GbdkAvailable)
        {
            return;
        }

        IRModule module = TestHarness.CompileModule(Source);
        RomBuildResult build = TestHarness.BuildRom(Source);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        IReadOnlyDictionary<string, int> sizes = FunctionSizes(build.RomPath!);

        Assert.NotEmpty(sizes);

        string[] order = ["Work_Tiny", "Work_Medium", "Work_Large"];

        var predicted = new List<int>();
        var measured = new List<int>();

        foreach (string name in order)
        {
            FunctionCost? cost = module.Costs.Functions.FirstOrDefault(f => f.Name == name);

            Assert.NotNull(cost);

            if (!sizes.TryGetValue(name, out int bytes))
            {
                // The symbol should be there, but a linker that inlined or folded
                // it away is not this test's business to fail over.
                continue;
            }

            predicted.Add(cost.Cycles);
            measured.Add(bytes);
        }

        Assert.Equal(3, predicted.Count);

        for (int i = 1; i < predicted.Count; i++)
        {
            Assert.True(
                predicted[i] > predicted[i - 1],
                $"the model should rank {order[i]} above {order[i - 1]}: {predicted[i - 1]} then {predicted[i]}");

            Assert.True(
                measured[i] > measured[i - 1],
                $"SDCC emitted {measured[i]} bytes for {order[i]} and {measured[i - 1]} for {order[i - 1]}; "
                + $"the model predicted {predicted[i - 1]} then {predicted[i]} cycles, so its ordering "
                + "no longer matches what is actually generated");
        }
    }

    /// <summary>
    /// Approximate code size per symbol, from the gaps between addresses in the
    /// linker's <c>.sym</c>.
    /// </summary>
    /// <remarks>
    /// Only bank 0, and only the gap to the next symbol, which counts any padding
    /// or literal pool that follows. Good enough to rank three functions an order
    /// of magnitude apart, and not good enough for anything finer, which is why
    /// this reads the file here rather than growing
    /// <c>SymbolMapReader</c>, whose job is answering where a symbol was placed.
    /// </remarks>
    private static IReadOnlyDictionary<string, int> FunctionSizes(string romPath)
    {
        string symbolPath = Path.ChangeExtension(romPath, ".sym");

        if (!File.Exists(symbolPath))
        {
            return new Dictionary<string, int>();
        }

        var placed = new List<(string Name, int Address)>();

        foreach (string line in File.ReadLines(symbolPath))
        {
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            int colon = line.IndexOf(':');
            int space = line.IndexOf(' ');

            if (colon != 2 || space < colon)
            {
                continue;
            }

            if (!int.TryParse(line.AsSpan(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bank)
                || bank != 0
                || !int.TryParse(
                    line.AsSpan(colon + 1, space - colon - 1),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out int address))
            {
                continue;
            }

            string name = line[(space + 1)..].Trim().TrimStart('_');

            // Code lives in ROM; anything above 0x8000 is RAM and not a function.
            if (name.Length > 0 && address < 0x8000)
            {
                placed.Add((name, address));
            }
        }

        placed.Sort((a, b) => a.Address.CompareTo(b.Address));

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < placed.Count - 1; i++)
        {
            int size = placed[i + 1].Address - placed[i].Address;

            if (size > 0)
            {
                sizes[placed[i].Name] = size;
            }
        }

        return sizes;
    }
}
