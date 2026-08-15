using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// Which loops GB# will state a trip count for, and which it refuses to guess at.
/// </summary>
/// <remarks>
/// The refusals matter more than the successes. A worst-case estimate that
/// silently invents a bound is worse than one that admits it does not have one,
/// because the number it produces looks exactly like a number that means
/// something.
/// </remarks>
public sealed class LoopBoundTests
{
    private static IReadOnlyList<LoopCost> Loops(string body, string extra = "")
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(body, extra));

        return CostModel.Measure(module.EntryPoint, LoopBounds.CollectionCapacities(module)).Loops;
    }

    private static LoopCost Only(string body, string extra = "")
    {
        IReadOnlyList<LoopCost> loops = Loops(body, extra);

        return Assert.Single(loops);
    }

    [Theory]
    [InlineData("for (byte i = 0; i < 10; i++)", 10)]
    [InlineData("for (byte i = 0; i <= 10; i++)", 11)]
    [InlineData("for (byte i = 0; i < 10; i += 2)", 5)]
    [InlineData("for (byte i = 0; i < 9; i += 2)", 5)]
    [InlineData("for (byte i = 10; i > 0; i--)", 10)]
    [InlineData("for (sbyte i = 10; i >= 0; i--)", 11)]
    [InlineData("for (byte i = 5; i < 5; i++)", 0)]
    public void AConstantBoundIsCounted(string header, int expected)
    {
        LoopCost loop = Only($"        {header} {{ Sprites[0].X = (byte)i; }}");

        Assert.Equal(expected, loop.TripCount);
        Assert.Equal(BoundSource.Constant, loop.Bound);
        Assert.Equal(loop.PerIterationCycles * expected, loop.TotalCycles);
    }

    /// <summary>
    /// FixedArray's Length folds to a constant during lowering, so this is really
    /// the constant case wearing a nicer name.
    /// </summary>
    [Fact]
    public void ALengthIsAConstantBound()
    {
        LoopCost loop = Only(
            "        for (byte i = 0; i < Data.Values.Length; i++) { Sprites[0].X = Data.Values[i]; }",
            """
            public static class Data
            {
                [Capacity(6)]
                public static FixedArray<byte> Values;
            }
            """);

        Assert.Equal(6, loop.TripCount);
    }

    /// <summary>
    /// The rule the whole feature turns on. Written the obvious way, a loop over a
    /// fixed list reads Count, which is a runtime field, and that is the shape
    /// Samples/Enemies actually uses. Count can never exceed Capacity, because the
    /// generated Add refuses past it, so Capacity is a sound upper bound.
    /// </summary>
    [Fact]
    public void ACountIsBoundedByTheCollectionsCapacity()
    {
        LoopCost loop = Only(
            "        for (byte i = 0; i < Data.Items.Count; i++) { Sprites[0].X = Data.Items[i]; }",
            """
            public static class Data
            {
                [Capacity(8)]
                public static FixedList<byte> Items;
            }
            """);

        Assert.Equal(8, loop.TripCount);
        Assert.Equal(BoundSource.Capacity, loop.Bound);
    }

    [Theory]
    [InlineData("while (true)")]
    [InlineData("while (Input.Right)")]
    [InlineData("for (;;)")]
    public void AnUnboundedLoopIsNotGuessedAt(string header)
    {
        LoopCost loop = Only($"        {header} {{ Sprites[0].X = 1; }}");

        Assert.Null(loop.TripCount);
        Assert.Null(loop.TotalCycles);
        Assert.Equal(BoundSource.None, loop.Bound);
    }

    /// <summary>
    /// A bound read from a mutable global is not a bound: the body could change it.
    /// </summary>
    [Fact]
    public void AVariableBoundIsNotABound()
    {
        LoopCost loop = Only(
            "        for (byte i = 0; i < Data.Limit; i++) { Sprites[0].X = i; }",
            """
            public static class Data
            {
                public static byte Limit;
            }
            """);

        Assert.Null(loop.TripCount);
    }

    /// <summary>
    /// This looks like eleven iterations and never terminates: an unsigned counter
    /// wraps to 255 rather than going below zero. Reporting eleven would be the
    /// most dangerous kind of wrong, a plausible figure for a loop with no end.
    /// </summary>
    [Fact]
    public void CountingDownThroughZeroOnAnUnsignedCounterIsNotCounted()
    {
        LoopCost loop = Only("        for (byte i = 10; i >= 0; i--) { Sprites[0].X = i; }");

        Assert.Null(loop.TripCount);
    }

    [Fact]
    public void ACounterTheBodyAssignsIsNotCounted()
    {
        LoopCost loop = Only("        for (byte i = 0; i < 10; i++) { i = 0; Sprites[0].X = i; }");

        Assert.Null(loop.TripCount);
    }

    [Fact]
    public void ACounterTheBodyIncrementsAgainIsNotCounted()
    {
        LoopCost loop = Only("        for (byte i = 0; i < 10; i++) { i++; Sprites[0].X = i; }");

        Assert.Null(loop.TripCount);
    }

    /// <summary>
    /// A break leaves the count an upper bound rather than an exact one, which is
    /// what a worst-case estimate wants. The help text says so; the bound stands.
    /// </summary>
    [Fact]
    public void ABreakLeavesTheCountAsACeiling()
    {
        LoopCost loop = Only("        for (byte i = 0; i < 10; i++) { if (Input.A) { break; } }");

        Assert.Equal(10, loop.TripCount);
    }

    [Fact]
    public void ABoundedLoopInsideAnUnboundedOneStillBinds()
    {
        IReadOnlyList<LoopCost> loops = Loops("""
                    while (true)
                    {
                        for (byte i = 0; i < 4; i++) { Sprites[0].X = i; }
                        Game.WaitVBlank();
                    }
            """);

        Assert.Equal(2, loops.Count);

        // Outermost first, so the frame loop leads.
        Assert.Null(loops[0].TripCount);
        Assert.True(loops[0].IsFrameLoop);

        Assert.Equal(4, loops[1].TripCount);
        Assert.Equal(4 * loops[1].PerIterationCycles, loops[1].TotalCycles);
    }

    /// <summary>
    /// Nesting multiplies, which is the case where a cost estimate earns its keep:
    /// neither number is alarming on its own.
    /// </summary>
    [Fact]
    public void NestedBoundedLoopsMultiply()
    {
        IReadOnlyList<LoopCost> loops = Loops("""
                    for (byte y = 0; y < 18; y++)
                    {
                        for (byte x = 0; x < 20; x++) { Sprites[0].X = x; }
                    }
            """);

        Assert.Equal(2, loops.Count);
        Assert.Equal(18, loops[0].TripCount);
        Assert.Equal(20, loops[1].TripCount);

        // The outer loop's per-iteration cost already contains the whole inner loop.
        Assert.True(loops[0].PerIterationCycles > loops[1].TotalCycles);
    }
}
