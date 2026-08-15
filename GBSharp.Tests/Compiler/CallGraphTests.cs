using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// What can reach what, and how deep that goes.
/// </summary>
/// <remarks>
/// Recursion is the reason this exists. It is legal in the GB# subset and was
/// undetected until now: nothing built a call graph, so a self-calling method
/// lowered, emitted and linked. On a machine whose stack grows down from the top
/// of work RAM into the same 8 KB the static fields grow up through, with no
/// bounds check anywhere, that is silent memory corruption.
/// </remarks>
public sealed class CallGraphTests
{
    private static CallGraph Graph(string source) => CallGraph.Build(TestHarness.CompileModule(source));

    /// <summary>A program whose Main calls the named helpers, plus their declarations.</summary>
    private static string Program(string body, string helpers) => TestHarness.Program(body, $$"""
        public static class Helpers
        {
        {{helpers}}
        }
        """);

    [Fact]
    public void ACallBecomesAnEdge()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Step();",
            "    public static void Step() { Sprites[0].X = 1; }"));

        Assert.Contains(graph.From(graph.EntryPoint), s => graph.DisplayName(s.Callee).Contains("Step"));
    }

    /// <summary>
    /// There is no IR function behind a native symbol, so there is nothing for the
    /// graph to reach. Its cost is charged by the cost model instead.
    /// </summary>
    [Fact]
    public void ANativeCallIsNotAnEdge()
    {
        CallGraph graph = Graph(TestHarness.Program("        Display.Enable(); Game.WaitVBlank();"));

        Assert.Empty(graph.From(graph.EntryPoint));
    }

    [Fact]
    public void DirectRecursionIsFound()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Down(3);",
            """
                public static void Down(byte n)
                {
                    if (n > 0) { Down((byte)(n - 1)); }
                }
            """));

        Assert.True(graph.HasRecursion);

        IReadOnlyList<string> cycle = Assert.Single(graph.Cycles);
        Assert.Single(cycle);
        Assert.Contains("Down", graph.DisplayName(cycle[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void MutualRecursionIsFound()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Ping(3);",
            """
                public static void Ping(byte n)
                {
                    if (n > 0) { Pong((byte)(n - 1)); }
                }

                public static void Pong(byte n)
                {
                    if (n > 0) { Ping((byte)(n - 1)); }
                }
            """));

        IReadOnlyList<string> cycle = Assert.Single(graph.Cycles);
        Assert.Equal(2, cycle.Count);
    }

    /// <summary>
    /// Two paths to the same function are not a cycle. Confusing the two would
    /// make ordinary shared helpers report as recursion.
    /// </summary>
    [Fact]
    public void ADiamondIsNotACycle()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Left(); Helpers.Right();",
            """
                public static void Shared() { Sprites[0].X = 1; }

                public static void Left() { Shared(); }

                public static void Right() { Shared(); }
            """));

        Assert.False(graph.HasRecursion);
        Assert.Empty(graph.Cycles);
    }

    /// <summary>
    /// Tarjan's algorithm is written iteratively here for exactly this reason: a
    /// stack analysis that overflows its own stack would be a poor joke.
    /// </summary>
    [Fact]
    public void ALongChainDoesNotOverflowTheTraversal()
    {
        const int depth = 400;

        var helpers = new System.Text.StringBuilder();

        for (int i = 0; i < depth; i++)
        {
            string next = i + 1 < depth ? $"Step{i + 1}();" : "Sprites[0].X = 1;";
            helpers.AppendLine($"    public static void Step{i}() {{ {next} }}");
        }

        CallGraph graph = Graph(Program("        Helpers.Step0();", helpers.ToString()));

        Assert.False(graph.HasRecursion);

        StackDepth stack = StackAnalysis.Measure(graph);

        Assert.True(stack.Bounded);
        Assert.Equal(depth + 1, stack.Calls);
    }

    [Fact]
    public void TheDeepestPathIsTheOneReported()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Shallow(); Helpers.Deep();",
            """
                public static void Shallow() { Sprites[0].X = 1; }

                public static void Deep() { Deeper(); }

                public static void Deeper() { Deepest(); }

                public static void Deepest() { Sprites[0].X = 1; }
            """));

        StackDepth stack = StackAnalysis.Measure(graph);

        Assert.True(stack.Bounded);
        Assert.Equal(4, stack.Calls);
        Assert.Contains("Deepest", stack.DeepestPath[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// No number at all, rather than a number derived from the acyclic part. The
    /// depth of a recursive program is whatever the data makes it, and a figure
    /// offered here would be quoted as a ceiling it is not.
    /// </summary>
    [Fact]
    public void ARecursiveProgramGetsNoDepthCeiling()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Down(3);",
            """
                public static void Down(byte n)
                {
                    if (n > 0) { Down((byte)(n - 1)); }
                }
            """));

        Assert.False(StackAnalysis.Measure(graph).Bounded);
    }

    /// <summary>
    /// A recursive function nothing calls is still a hazard, but it does not make
    /// the depth of a program that never reaches it unbounded.
    /// </summary>
    [Fact]
    public void UnreachableRecursionDoesNotUnboundTheProgram()
    {
        CallGraph graph = Graph(Program(
            "        Sprites[0].X = 1;",
            """
                public static void Down(byte n)
                {
                    if (n > 0) { Down((byte)(n - 1)); }
                }
            """));

        Assert.True(graph.HasRecursion);
        Assert.True(StackAnalysis.Measure(graph).Bounded);
    }

    [Fact]
    public void ACallSiteRecordsTheStatementItWasMadeFrom()
    {
        CallGraph graph = Graph(Program(
            "        Helpers.Step();",
            "    public static void Step() { Sprites[0].X = 1; }"));

        CallSite site = Assert.Single(graph.From(graph.EntryPoint));

        Assert.False(site.Span.IsNone);
        Assert.False(site.InFrameLoop);
        Assert.False(site.CrossesBank);
    }

    [Fact]
    public void ACallInsideTheFrameLoopIsMarkedAsSuch()
    {
        CallGraph graph = Graph(Program(
            """
                    Helpers.Once();

                    while (true)
                    {
                        Helpers.EveryFrame();
                        Game.WaitVBlank();
                    }
            """,
            """
                public static void Once() { Sprites[0].X = 1; }

                public static void EveryFrame() { Sprites[0].Y = 1; }
            """));

        IReadOnlyList<CallSite> sites = graph.From(graph.EntryPoint);

        Assert.False(sites.Single(s => graph.DisplayName(s.Callee).Contains("Once")).InFrameLoop);
        Assert.True(sites.Single(s => graph.DisplayName(s.Callee).Contains("EveryFrame")).InFrameLoop);
    }

    /// <summary>
    /// A while(true) with no VBlank wait is not the frame loop. Treating every
    /// unbounded loop as one would attach the frame budget to loops that have
    /// nothing to do with a frame.
    /// </summary>
    [Fact]
    public void AnUnboundedLoopWithNoFrameBarrierIsNotTheFrameLoop()
    {
        CallGraph graph = Graph(Program(
            """
                    while (true)
                    {
                        Helpers.Spin();
                    }
            """,
            "    public static void Spin() { Sprites[0].X = 1; }"));

        Assert.All(graph.From(graph.EntryPoint), s => Assert.False(s.InFrameLoop));
    }

    [Fact]
    public void ACallIntoAnotherBankCrossesIt()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Level.Load();",
            """
            [Bank(2)]
            public static class Level
            {
                public static void Load() { Sprites[0].X = 1; }
            }
            """));

        CallGraph graph = CallGraph.Build(module);

        Assert.True(Assert.Single(graph.From(graph.EntryPoint)).CrossesBank);
    }
}
