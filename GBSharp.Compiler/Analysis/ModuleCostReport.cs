namespace GBSharp.Compiler.Analysis;

/// <summary>
/// What the analysis found, for the build report to present.
/// </summary>
/// <param name="Functions">
/// Every function's estimate, most expensive first. Includes compiler-generated
/// ones; consumers that rank for a human should filter on
/// <see cref="FunctionCost.IsCompilerGenerated"/>.
/// </param>
/// <param name="FrameLoop">
/// The frame loop's per-iteration estimate, or null if the program has none.
/// </param>
/// <param name="Stack">Worst-case call depth.</param>
/// <param name="StaticWramBytes">Bytes of work RAM the source declared.</param>
/// <param name="StaticRomBytes">Bytes of ROM the source declared, excluding assets.</param>
/// <remarks>
/// <para>
/// Carried on the module rather than returned separately, for the same reason
/// the converted assets are: the report is assembled well after lowering, by a
/// caller that should not have to thread a second value through to get it. The
/// two byte totals ride along because both report paths were computing them
/// independently, and one of the two had already drifted.
/// </para>
/// <para>
/// Every figure here except the byte counts is an estimate. See
/// <see cref="Sm83CostTable"/> for the error bar.
/// </para>
/// </remarks>
public sealed record ModuleCostReport(
    IReadOnlyList<FunctionCost> Functions,
    LoopCost? FrameLoop,
    StackDepth Stack,
    int StaticWramBytes,
    int StaticRomBytes)
{
    /// <summary>
    /// Nothing measured, so the report prints none of these sections.
    /// </summary>
    /// <remarks>
    /// A value rather than null, so no consumer needs a null check, the same
    /// reason <c>Budgets.None</c> exists.
    /// </remarks>
    public static ModuleCostReport Empty { get; } = new([], null, StackDepth.Unknown, 0, 0);

    /// <summary>True if there is nothing worth printing.</summary>
    public bool IsEmpty => Functions.Count == 0 && FrameLoop is null;
}

/// <summary>
/// The deepest chain of calls the program can make.
/// </summary>
/// <param name="Calls">
/// Frames on the deepest path. Zero when nothing was measured.
/// </param>
/// <param name="Bounded">
/// False if the program can recurse, in which case <paramref name="Calls"/>
/// describes only the acyclic part and must not be presented as a maximum.
/// </param>
/// <param name="DeepestPath">
/// The functions on that path, entry point first, by their C# names.
/// </param>
/// <remarks>
/// <para>
/// Depth in calls rather than bytes of stack, deliberately. Depth is exact: GB#
/// has no delegates and no indirect calls, so the call graph is the whole truth
/// about what can reach what. Bytes would not be: GB# never sees SDCC's frame
/// layout, its register allocation or its spills, and the resulting figure would
/// be wrong by a factor of two to four and biased low, which is the direction
/// that lets a ROM ship and then corrupt memory.
/// </para>
/// <para>
/// The measured stack figure comes from the linker instead, where the toolchain
/// is available, and is reported as the measurement it is.
/// </para>
/// </remarks>
public sealed record StackDepth(int Calls, bool Bounded, IReadOnlyList<string> DeepestPath)
{
    /// <summary>Nothing measured.</summary>
    public static StackDepth Unknown { get; } = new(0, Bounded: true, []);
}
