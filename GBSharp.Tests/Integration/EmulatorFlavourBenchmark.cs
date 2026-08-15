using System.Diagnostics;
using GBSharp.Emulator;

namespace GBSharp.Tests.Integration;

/// <summary>
/// What instrumentation costs, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// The two-flavour design was taken on the stated risk that the hooked build
/// might be too slow for the test harness to use, in which case the harness
/// would want the fast one and the tooling the hooked one. That risk sat
/// unmeasured through four milestones while every test ran on the debug
/// flavour, so this measures it.
/// </para>
/// <para>
/// A process loads one flavour, so this cannot compare them in a single run.
/// It reports the flavour it has against a fixed workload, and
/// <c>GBSHARP_EMULATOR_FLAVOUR</c> selects the other for a second run. Asserting
/// on a ratio would need both at once; asserting on wall-clock time would fail
/// on a loaded CI box. So it asserts only what is worth failing a build over:
/// that emulation is faster than the machine it emulates, and prints the rest.
/// </para>
/// </remarks>
[Collection(EmulatorStateCollection.Name)]
public sealed class EmulatorFlavourBenchmark
{
    /// <summary>One second of emulated time.</summary>
    private const int Frames = 60;

    /// <summary>
    /// Enough to swamp start-up costs without making a suite slow.
    /// </summary>
    private const int Seconds = 10;

    [Fact]
    public void EmulationOutrunsTheMachineItEmulates()
    {
        if (!GameBoyTest.EmulatorAvailable)
        {
            return;
        }

        using GameBoy game = Machine();

        // Warm the JIT and the first-touch page faults, which otherwise land
        // inside the measurement and are not what is being measured.
        game.RunFrames(Frames);

        var clock = Stopwatch.StartNew();
        game.RunFrames(Frames * Seconds);
        clock.Stop();

        double realtime = Seconds / clock.Elapsed.TotalSeconds;

        Console.WriteLine(
            $"{EmulatorRuntime.LoadedFlavour} flavour: {Seconds}s of emulated time in " +
            $"{clock.Elapsed.TotalMilliseconds:0}ms, {realtime:0.0}x realtime " +
            $"({clock.Elapsed.TotalMilliseconds / (Frames * Seconds):0.00}ms per frame, " +
            $"budget 16.74ms)");

        // The only claim worth failing a build over. A flavour that cannot keep
        // up with the hardware cannot run a game, and everything above this bar
        // is a number to read rather than a threshold to enforce.
        Assert.True(
            realtime > 1.0,
            $"the {EmulatorRuntime.LoadedFlavour} flavour ran at {realtime:0.0}x realtime, " +
            "which is slower than the Game Boy it is emulating");
    }

    /// <summary>
    /// Turning the profiler on is close to free, because the expensive part is
    /// already being paid for.
    /// </summary>
    /// <remarks>
    /// The per-instruction hook fires in this flavour whether profiling is on
    /// or not; the flag only gates two array writes inside it. So the cost of
    /// profiling is the cost of the debug flavour, which the test above
    /// measures, and the switch exists to bound memory and to keep one scene's
    /// numbers from being polluted by another's, not to buy back speed.
    /// </remarks>
    [Fact]
    public void EnablingTheProfilerIsNearlyFreeInAFlavourThatAlreadyHooks()
    {
        if (!GameBoyTest.EmulatorAvailable || EmulatorRuntime.LoadedFlavour != EmulatorFlavour.Debug)
        {
            return;
        }

        using GameBoy game = Machine();
        game.RunFrames(Frames);

        GameBoy.SetProfilingEnabled(false);
        double off = Measure(game);

        GameBoy.SetProfilingEnabled(true);
        GameBoy.ClearProfile();
        double on = Measure(game);
        GameBoy.SetProfilingEnabled(false);
        GameBoy.ClearProfile();

        Console.WriteLine(
            $"profiling off: {off:0.0}x realtime, on: {on:0.0}x realtime, " +
            $"cost {(off / on - 1) * 100:0}%");

        // Not a tight bound, and deliberately not a claim that on is slower
        // than off: measured, the difference is inside the noise. What would be
        // worth failing over is profiling making the emulator unusable.
        Assert.True(on > 1.0, $"profiling dropped the emulator to {on:0.0}x realtime");
    }

    /// <summary>
    /// A machine on whichever flavour the environment asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="GameBoyTest"/>, which pins the debug flavour
    /// so that the rest of the suite is inspectable. Measuring the cost of
    /// instrumentation is the one place that has to be free to load the other
    /// one, which <c>GBSHARP_EMULATOR_FLAVOUR=fast</c> selects.
    /// </para>
    /// <para>
    /// A flavour already loaded wins over both, because a process holds exactly
    /// one and taking the other would not merely fail here: it would strand
    /// every test that runs afterwards. Within a suite this therefore measures
    /// the debug flavour along with everyone else, and measuring the fast one
    /// means running this class on its own.
    /// </para>
    /// </remarks>
    private static GameBoy Machine()
    {
        if (EmulatorRuntime.LoadedFlavour is null)
        {
            EmulatorRuntime.Load(
                Environment.GetEnvironmentVariable(EmulatorRuntime.FlavourVariable)
                    is { Length: > 0 } value &&
                value.Equals("fast", StringComparison.OrdinalIgnoreCase)
                    ? EmulatorFlavour.Fast
                    : EmulatorFlavour.Debug);
        }

        return GameBoy.Load(BusyRom());
    }

    private static double Measure(GameBoy game)
    {
        var clock = Stopwatch.StartNew();
        game.RunFrames(Frames * Seconds);
        clock.Stop();
        return Seconds / clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// A ROM that never halts, so the CPU executes every tick of every frame.
    /// </summary>
    /// <remarks>
    /// A game that waits for VBlank spends most of a frame halted, and halted
    /// ticks cost an emulator almost nothing, so timing one would measure the
    /// PPU and report that instrumentation is free. Zeroes decode as NOP, and a
    /// ROM full of them keeps the CPU decoding for the whole frame, which is
    /// the workload the hooks actually run on.
    /// </remarks>
    private static byte[] BusyRom() => new byte[32 * 1024];
}
