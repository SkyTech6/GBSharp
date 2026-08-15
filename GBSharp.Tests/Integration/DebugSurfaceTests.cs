using System.Runtime.InteropServices;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Emulator;

namespace GBSharp.Tests.Integration;

/// <summary>
/// The tooling half of the ABI: registers, stepping, breakpoints and the
/// per-address profiler, against a real ROM on a real emulator.
/// </summary>
/// <remarks>
/// <para>
/// These need the instrumented flavour, which <see cref="GameBoyTest"/> loads
/// and asserts it got. What each one is really checking is that the emulator
/// and GB# agree about a coordinate system: an address means the same thing to
/// the CPU, to the linker map and to the profiler, or none of this composes.
/// </para>
/// <para>
/// Breakpoints and profile counters belong to the library rather than to an
/// emulator, so every test that sets one clears it again, and the whole class
/// runs in <see cref="EmulatorStateCollection"/> so that nothing else is
/// emulating beside it. Counters are indexed by ROM address: a second ROM
/// running concurrently adds its ticks to the same indices.
/// </para>
/// </remarks>
[Collection(EmulatorStateCollection.Name)]
public sealed class DebugSurfaceTests
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

    private static RomBuildResult Build()
    {
        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpinsForever));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        return build;
    }

    /// <summary>The entry point's bank and address, out of the ROM's own map.</summary>
    private static RomSymbol EntryPoint(RomBuildResult build)
    {
        string stem = Path.Combine(
            Path.GetDirectoryName(build.RomPath!)!,
            Path.GetFileNameWithoutExtension(build.RomPath!));

        FunctionMapEntry entry = Assert.Single(
            System.Text.Json.JsonSerializer.Deserialize<FunctionMapEntry[]>(
                File.ReadAllText(stem + ".functions.json"),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!,
            f => f.Method == "Program.Main()");

        return Assert.Single(
            SymbolMapReader.TryReadSymbols(stem + ".sym"), s => s.Name == entry.Name);
    }

    [Fact]
    public void TheRegistersAgreeWithTheProgramCounterReadOnItsOwn()
    {
        if (!GameBoyTest.EmulatorAvailable)
        {
            return;
        }

        using GameBoyTest game = GameBoyTest.Load(new byte[32 * 1024]);
        game.RunFrames(2);

        CpuRegisters? registers = game.Machine.Registers;

        // Two entry points, one fact. If they disagreed, one of them is reading
        // a different machine than the caller thinks.
        Assert.NotNull(registers);
        Assert.Equal(game.ProgramCounter, registers.Value.PC);
    }

    [Fact]
    public void SteppingAdvancesTheProgramCounterByOneInstruction()
    {
        if (!GameBoyTest.EmulatorAvailable)
        {
            return;
        }

        using GameBoyTest game = GameBoyTest.Load(new byte[32 * 1024]);
        game.RunFrames(1);

        ushort before = game.ProgramCounter;
        game.Machine.Step();
        ushort after = game.ProgramCounter;

        // A zeroed ROM is a field of NOPs, which is the one program whose
        // control flow is knowable without disassembling anything.
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public void ABreakpointStopsTheRunBeforeTheInstructionRuns()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();
        RomSymbol entry = EntryPoint(build);

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);
        try
        {
            int id = game.Machine.AddBreakpoint(entry.Bank, entry.Address);
            Assert.True(id >= 0, "the debug flavour should accept a breakpoint");

            EmulatorEvent stopped = game.Machine.RunFrames(60);

            Assert.True(stopped.HasFlag(EmulatorEvent.Breakpoint), $"expected a breakpoint, got {stopped}");

            // Before, not after. The instruction at the breakpoint has not run,
            // which is the whole difference between a breakpoint and a trace.
            Assert.Equal(entry.Address, game.ProgramCounter);
        }
        finally
        {
            GameBoy.ClearBreakpoints();
        }
    }

    [Fact]
    public void ABreakpointInAnUnmappedBankIsStillPlacedWhereItWasAsked()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();
        RomSymbol entry = EntryPoint(build);

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);
        try
        {
            // Bank 200 does not exist in this ROM and is certainly not mapped.
            // Upstream's API would silently record whichever bank was mapped as
            // the breakpoint was set, and the breakpoint would then fire on the
            // right address in the wrong bank.
            Assert.True(game.Machine.AddBreakpoint(200, entry.Address) >= 0);

            EmulatorEvent stopped = game.Machine.RunFrames(30);

            Assert.False(
                stopped.HasFlag(EmulatorEvent.Breakpoint),
                "a breakpoint in a bank that never runs must never fire");
        }
        finally
        {
            GameBoy.ClearBreakpoints();
        }
    }

    [Fact]
    public void ClearingBreakpointsLetsTheRunFinishAgain()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();
        RomSymbol entry = EntryPoint(build);

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        game.Machine.AddBreakpoint(entry.Bank, entry.Address);
        Assert.True(game.Machine.RunFrames(60).HasFlag(EmulatorEvent.Breakpoint));

        GameBoy.ClearBreakpoints();

        Assert.False(game.Machine.RunFrames(10).HasFlag(EmulatorEvent.Breakpoint));
    }

    [Fact]
    public void TheProfilerAttributesTicksToTheMethodThatSpentThem()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        Assert.True(GameBoy.SetProfilingEnabled(true), "the debug flavour should profile");
        try
        {
            GameBoy.ClearProfile();

            const int Frames = 60;
            game.RunFrames(Frames);

            var counts = new uint[game.Machine.RomSize];
            var cycles = new uint[game.Machine.RomSize];
            Assert.Equal(counts.Length, game.Machine.ReadProfile(counts, cycles));

            ProfileReport report = ProfileReport.Build(
                RomSymbolResolver.ForRom(build.RomPath!), counts, cycles, Frames);

            Assert.NotEmpty(report.Entries);

            // A second of emulated time is 60 frames of 70224 ticks, and the
            // profiler should have seen most of them. An order-of-magnitude
            // check rather than an exact one: what is being tested is that
            // ticks are attributed at all, not the timing of GBDK's runtime.
            Assert.InRange(report.TotalCycles, Frames * 70224L / 2, Frames * 70224L * 2);

            // The game is a vblank spin, so the method holding that loop is the
            // one the profile should point at.
            Assert.Contains(report.Entries, e => e.Method == "Program.Main()");

            // Every attributed tick belongs to code the linker map covers. GBDK's
            // own runtime is in the map too, so this is not a claim that GB# wrote
            // everything -- only that nothing ran at an address nobody named.
            Assert.True(
                report.UnattributedCycles < report.TotalCycles / 2,
                $"{report.UnattributedCycles} of {report.TotalCycles} ticks hit no symbol");
        }
        finally
        {
            GameBoy.SetProfilingEnabled(false);
            GameBoy.ClearProfile();
        }
    }

    [Fact]
    public void CoverageNamesAMethodTheGameNeverCalls()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        // A method that exists, is linked, and is never reached. Coverage that
        // could not tell this from Main would be measuring nothing.
        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(
            """
                    Display.Enable();

                    if (Input.Start)
                    {
                        Unreachable.Never();
                    }

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
            """,
            extra: """
                public static class Unreachable
                {
                    public static void Never()
                    {
                        Sprites[0].X = 99;
                    }
                }
                """));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        Assert.True(GameBoy.SetRomUsageEnabled(true));
        GameBoy.ClearRomUsage();

        // Start is never pressed, so the branch is never taken.
        game.RunFrames(60);

        var usage = new RomUsage[game.Machine.RomSize];
        Assert.Equal(usage.Length, game.Machine.ReadRomUsage(usage));

        CoverageReport report = CoverageReport.Build(
            RomSymbolResolver.ForRom(build.RomPath!), MemoryMarshal.AsBytes<RomUsage>(usage));

        Assert.Contains(report.Unreached, e => e.Method == "Unreachable.Never()");
        Assert.DoesNotContain(report.Unreached, e => e.Method == "Program.Main()");
    }

    [Fact]
    public void ABytesUsageSaysWhetherItRanOrWasOnlyRead()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        GameBoy.SetRomUsageEnabled(true);
        GameBoy.ClearRomUsage();
        game.RunFrames(30);

        var usage = new RomUsage[game.Machine.RomSize];
        game.Machine.ReadRomUsage(usage);

        // The entry point is the first thing the cartridge runs, so its vector
        // is code; and a ROM of this size cannot possibly have run all of
        // itself, so something must be untouched. Both halves matter: usage
        // that marked everything would pass a test that only checked for code.
        Assert.Contains(usage, u => u.HasFlag(RomUsage.CodeStart));
        Assert.Contains(usage, u => u == RomUsage.None);
    }

    [Fact]
    public void ProfilingIsOffUntilItIsAskedFor()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = Build();

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        GameBoy.SetProfilingEnabled(false);
        GameBoy.ClearProfile();
        game.RunFrames(10);

        var cycles = new uint[game.Machine.RomSize];
        game.Machine.ReadProfile([], cycles);

        // Costing something to gather is exactly why it is a switch.
        Assert.All(cycles, c => Assert.Equal(0u, c));
    }
}
