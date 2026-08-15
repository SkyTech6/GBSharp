using System.Text.Json;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Integration;

/// <summary>
/// An address in a running ROM, named as the developer's C#.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Backend.RomSymbolResolverTests"/> pins the resolver against
/// artefacts written by hand. These pin it against artefacts written by a real
/// build and addresses read out of a real emulator, which is the only way to
/// catch the two of them disagreeing about a format neither test invented.
/// </para>
/// <para>
/// The chain is: the emulator reports a program counter and the bank under it,
/// the linker's <c>.sym</c> names the code at that bank and address, and
/// <c>.functions.json</c> says which C# method that code was lowered from.
/// </para>
/// </remarks>
public sealed class SymbolChainTests
{
    private const string SpinsForever = """
                Display.Enable();

                while (true)
                {
                    Game.WaitVBlank();
                }
        """;

    private static bool SkipWithoutBoth() =>
        !TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable;

    private static string ArtefactPath(RomBuildResult build, string extension) =>
        Path.Combine(
            Path.GetDirectoryName(build.RomPath!)!,
            Path.GetFileNameWithoutExtension(build.RomPath!) + extension);

    [Fact]
    public void TheFunctionMapIsWrittenBesideTheRomOnAnOrdinaryBuild()
    {
        if (!TestHarness.GbdkAvailable)
        {
            return;
        }

        // Deliberately without --annotate-source. The source map is opt-in; this
        // is not, because a chain that works on some builds and not others fails
        // in a way that looks like a missing symbol rather than a missing flag.
        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpinsForever));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        string path = ArtefactPath(build, ".functions.json");
        Assert.True(File.Exists(path), ".functions.json should be written next to the ROM");

        // Deserialised with the reflection-based overload rather than
        // FunctionMapJson, for the same reason RomBuildTests reads the source
        // map that way: the artefact is meant to be readable by any tool, not
        // only by one that can see GB#'s own serializer context.
        FunctionMapEntry[]? entries = JsonSerializer.Deserialize<FunctionMapEntry[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(entries);
        Assert.Contains(entries, entry => entry.Method == "Program.Main()");
        Assert.All(entries, entry => Assert.False(string.IsNullOrEmpty(entry.Name)));
    }

    [Fact]
    public void TheEntryPointsAddressResolvesBackToTheMethodThatWroteIt()
    {
        if (!TestHarness.GbdkAvailable)
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpinsForever));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        // The C name is looked up rather than spelled out: how GB# mangles a
        // method into a C identifier is the emitter's business, and a test that
        // hard-coded it would fail on a rename that broke nothing.
        FunctionMapEntry[] functions = JsonSerializer.Deserialize<FunctionMapEntry[]>(
            File.ReadAllText(ArtefactPath(build, ".functions.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        FunctionMapEntry entry = Assert.Single(functions, f => f.Method == "Program.Main()");

        RomSymbol symbol = Assert.Single(
            SymbolMapReader.TryReadSymbols(ArtefactPath(build, ".sym")),
            s => s.Name == entry.Name);

        CodeLocation? location =
            RomSymbolResolver.ForRom(build.RomPath!).Resolve(symbol.Bank, symbol.Address);

        Assert.NotNull(location);
        Assert.Equal(entry.Name, location.Symbol);
        Assert.Equal("Program.Main()", location.Method);
        Assert.EndsWith("Program.cs", location.File);
        Assert.True(location.Line > 0, "the entry point should carry a source line");
    }

    [Fact]
    public void TheEmulatorsProgramCounterLandsOnCodeTheMapCanName()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpinsForever));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomSymbolResolver resolver = RomSymbolResolver.ForRom(build.RomPath!);
        Assert.False(resolver.IsEmpty);

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        // Sampled over many frames rather than once. Where the PC sits at a
        // frame boundary is a fact about GBDK's vblank handling, not about GB#,
        // and asserting on one sample would pin a detail this test does not own.
        var named = new List<CodeLocation>();
        for (int frame = 0; frame < 120; frame++)
        {
            game.RunFrames(1);

            ushort pc = game.ProgramCounter;
            if (resolver.Resolve(game.RomBankAt(pc), pc) is { } location)
            {
                named.Add(location);
            }
        }

        // The claim is that the three artefacts agree about the address space:
        // the emulator's PC, the bank it reports for it, and what the linker
        // said is there. A PC that resolved to nothing every single time would
        // mean they do not.
        Assert.NotEmpty(named);
    }

    [Fact]
    public void CodeOutsideTheCartridgeIsReportedAsSuchRatherThanMisnamed()
    {
        if (!GameBoyTest.EmulatorAvailable)
        {
            return;
        }

        // 32KB of zeroes is a cartridge as far as the ABI is concerned, and
        // this needs a machine rather than a program.
        using GameBoyTest game = GameBoyTest.Load(new byte[32 * 1024]);

        // HRAM. Nothing in a ROM map covers it, and the emulator has to say so
        // with something that is not a bank number, because bank 0 is real.
        Assert.Equal(-1, game.RomBankAt(0xFF80));
    }
}
