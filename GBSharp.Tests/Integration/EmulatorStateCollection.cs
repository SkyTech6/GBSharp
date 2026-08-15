namespace GBSharp.Tests.Integration;

/// <summary>
/// Tests that own the emulator process, because what they read is not theirs
/// alone.
/// </summary>
/// <remarks>
/// <para>
/// Breakpoints and the profiler are library state rather than emulator state:
/// the core keeps them in statics, so every <c>GameBoy</c> in a process shares
/// one set of breakpoints and one set of per-address counters. That is a fact
/// about the design, not an accident of it: <c>emulator-debug.c</c> was
/// written for a debugger driving one machine.
/// </para>
/// <para>
/// The consequence for tests is that a profile is only meaningful while nothing
/// else is running. Counters are indexed by ROM address, so a second ROM
/// executing concurrently adds its ticks to the same indices and the totals
/// stop meaning anything. <c>DisableParallelization</c> is how that gets said:
/// this collection never runs beside another.
/// </para>
/// <para>
/// It is also a real constraint on callers, not only on tests. A tool that
/// profiles has to be the only thing emulating in its process, which is why
/// <c>gbsharp profile</c> runs one ROM and exits.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EmulatorStateCollection
{
    public const string Name = "Emulator process state";
}
