using GBSharp.Compiler;
using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Tests.Diagnostics;

/// <summary>
/// What the cycle-cost band says, and, mostly, when it stays quiet.
/// </summary>
/// <remarks>
/// The negatives carry the weight here. GB# already reports a note per global,
/// per asset and per bank on every build; a fourth family that fired on every
/// loop would bury the errors among them and get the whole band silenced, which
/// would cost the developer the notes that mattered. So the thresholds are as
/// much a part of the design as the messages, and they are asserted.
/// </remarks>
public sealed class CycleCostDiagnosticTests
{
    private static IReadOnlyList<GBDiagnostic> Diagnostics(string body, string extra = "")
    {
        CompilationResult result = TestHarness.Compile(TestHarness.Program(body, extra));

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));

        return result.Diagnostics;
    }

    /// <summary>A helper class whose methods Main can call.</summary>
    private static string Helpers(string members) => $$"""
        public static class Helpers
        {
        {{members}}
        }
        """;

    // -----------------------------------------------------------------------
    // Recursion
    // -----------------------------------------------------------------------

    [Fact]
    public void RecursionIsReported()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics(
            "        Helpers.Down(3);",
            Helpers("""
                    public static void Down(byte n)
                    {
                        if (n > 0) { Down((byte)(n - 1)); }
                    }
                """));

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0058");

        Assert.Contains("Down", reported.Message, StringComparison.Ordinal);
        Assert.Contains("calls itself", reported.Message, StringComparison.Ordinal);
        Assert.Equal(GBSeverity.Warning, reported.Severity);
    }

    [Fact]
    public void MutualRecursionNamesTheChain()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics(
            "        Helpers.Ping(3);",
            Helpers("""
                    public static void Ping(byte n)
                    {
                        if (n > 0) { Pong((byte)(n - 1)); }
                    }

                    public static void Pong(byte n)
                    {
                        if (n > 0) { Ping((byte)(n - 1)); }
                    }
                """));

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0058");

        Assert.Contains("->", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A graph property belongs in the language band. The cycle-cost band is for
    /// estimates, and this is exact: the band test would force it to declare a
    /// category that misdescribes it.
    /// </summary>
    [Fact]
    public void RecursionIsNotACycleCostDiagnostic()
    {
        Assert.Equal(GBDiagnosticCategory.Language, GBDiagnostics.RecursiveCall.Category);
    }

    [Fact]
    public void AProgramThatDoesNotRecurseIsNotToldAboutRecursion()
    {
        TestHarness.AssertNotReported(
            Diagnostics("        Helpers.Step();", Helpers("    public static void Step() { Sprites[0].X = 1; }")),
            "GBS0058");
    }

    // -----------------------------------------------------------------------
    // Call depth
    // -----------------------------------------------------------------------

    [Fact]
    public void ADeepCallChainIsReported()
    {
        var members = new System.Text.StringBuilder();

        for (int i = 0; i < 8; i++)
        {
            string next = i + 1 < 8 ? $"Step{i + 1}();" : "Sprites[0].X = 1;";
            members.AppendLine($"    public static void Step{i}() {{ {next} }}");
        }

        GBDiagnostic reported = TestHarness.AssertReported(
            Diagnostics("        Helpers.Step0();", Helpers(members.ToString())),
            "GBS0420");

        Assert.Contains("->", reported.Message, StringComparison.Ordinal);
        Assert.Equal(GBSeverity.Resource, reported.Severity);
    }

    [Fact]
    public void AShallowProgramIsNotToldAboutItsStack() =>
        TestHarness.AssertNotReported(Diagnostics("        Sprites[0].X = 1;"), "GBS0420");

    /// <summary>
    /// A recursive program's depth is whatever the data makes it. A figure taken
    /// from the acyclic part would be read as a ceiling it is not, so there is
    /// none: GBS0058 has already explained why.
    /// </summary>
    [Fact]
    public void ARecursiveProgramIsGivenNoDepthCeiling()
    {
        var members = new System.Text.StringBuilder();

        for (int i = 0; i < 8; i++)
        {
            string next = i + 1 < 8 ? $"Step{i + 1}();" : "Step0();";
            members.AppendLine($"    public static void Step{i}() {{ {next} }}");
        }

        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics("        Helpers.Step0();", Helpers(members.ToString()));

        TestHarness.AssertReported(diagnostics, "GBS0058");
        TestHarness.AssertNotReported(diagnostics, "GBS0420");
    }

    // -----------------------------------------------------------------------
    // Loops
    // -----------------------------------------------------------------------

    [Fact]
    public void AnExpensiveBoundedLoopIsReported()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            Diagnostics("""
                        for (byte y = 0; y < 18; y++)
                        {
                            for (byte x = 0; x < 20; x++) { Sprites[0].X = x; }
                        }
                """),
            "GBS0410");

        Assert.Contains("estimated", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheapLoopIsNotReported() =>
        TestHarness.AssertNotReported(
            Diagnostics("        for (byte i = 0; i < 3; i++) { Sprites[0].X = i; }"),
            "GBS0410");

    /// <summary>
    /// The rule that keeps the whole band honest. An unbounded loop has no total,
    /// and there is no code path that could invent one, so the canonical GB# game
    /// loop can never be told it costs infinity, or any other number.
    /// </summary>
    [Fact]
    public void AnUnboundedLoopIsNeverGivenATotal()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics("""
                    while (true)
                    {
                        for (byte i = 0; i < 3; i++) { Sprites[0].X = i; }
                        Game.WaitVBlank();
                    }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0410");
    }

    /// <summary>
    /// A spin loop with no frame barrier is not the frame loop, and gets no frame
    /// budget attached to it. Silence, rather than a number about a frame it has
    /// nothing to do with.
    /// </summary>
    [Fact]
    public void AnUnboundedLoopWithNoFrameBarrierSaysNothing()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics("""
                    while (true)
                    {
                        Sprites[0].X = 1;
                    }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0401");
        TestHarness.AssertNotReported(diagnostics, "GBS0410");
    }

    // -----------------------------------------------------------------------
    // Frame budget
    // -----------------------------------------------------------------------

    [Fact]
    public void AFrameLoopThatFillsAFrameIsReported()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            Diagnostics("""
                        while (true)
                        {
                            for (byte y = 0; y < 200; y++)
                            {
                                for (byte x = 0; x < 200; x++) { Sprites[0].X = x; }
                            }

                            Game.WaitVBlank();
                        }
                """),
            "GBS0401");

        Assert.Contains("% of a frame", reported.Message, StringComparison.Ordinal);
        Assert.Contains("estimated", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>The thesis MVP verbatim. It must not be told it is in trouble.</summary>
    [Fact]
    public void AnOrdinaryFrameLoopIsNotReported()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = Diagnostics("""
                    Display.Enable();

                    byte x = 80;

                    while (true)
                    {
                        if (Input.Right)
                        {
                            x++;
                        }

                        Sprites[0].X = x;
                        Game.WaitVBlank();
                    }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0401");
    }

    // -----------------------------------------------------------------------
    // Bank hints
    // -----------------------------------------------------------------------

    [Fact]
    public void ABankedCallOnTheFramePathIsReported()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            Diagnostics("""
                        while (true)
                        {
                            Level.Load();
                            Game.WaitVBlank();
                        }
                """,
                """
                [Bank(2)]
                public static class Level
                {
                    public static void Load() { Sprites[0].X = 1; }
                }
                """),
            "GBS0440");

        Assert.Contains("bank 2", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost is paid wherever it sits on the path that runs every frame, not
    /// only at a call the loop makes directly.
    /// </summary>
    [Fact]
    public void ABankedCallReachedIndirectlyFromTheFrameLoopIsStillReported()
    {
        TestHarness.AssertReported(
            Diagnostics("""
                        while (true)
                        {
                            Helpers.Update();
                            Game.WaitVBlank();
                        }
                """,
                """
                public static class Helpers
                {
                    public static void Update() { Level.Load(); }
                }

                [Bank(2)]
                public static class Level
                {
                    public static void Load() { Sprites[0].X = 1; }
                }
                """),
            "GBS0440");
    }

    /// <summary>
    /// Setup code runs once. Charging it as a per-frame cost would be the kind of
    /// false positive that gets a band silenced.
    /// </summary>
    [Fact]
    public void ABankedCallOutsideTheFrameLoopIsNotReported()
    {
        TestHarness.AssertNotReported(
            Diagnostics("""
                        Level.Load();

                        while (true)
                        {
                            Game.WaitVBlank();
                        }
                """,
                """
                [Bank(2)]
                public static class Level
                {
                    public static void Load() { Sprites[0].X = 1; }
                }
                """),
            "GBS0440");
    }

    [Fact]
    public void AResidentCallIsNeverABankedCall() =>
        TestHarness.AssertNotReported(
            Diagnostics("""
                        while (true)
                        {
                            Helpers.Step();
                            Game.WaitVBlank();
                        }
                """,
                Helpers("    public static void Step() { Sprites[0].X = 1; }")),
            "GBS0440");

    [Fact]
    public void ACalleeWhoseCallersAllShareOneBankIsReported()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            Diagnostics("        Caller.Run();", """
                [Bank(1)]
                public static class Caller
                {
                    public static void Run() { Target.Work(); }
                }

                [Bank(2)]
                public static class Target
                {
                    public static void Work() { Sprites[0].X = 1; }
                }
                """),
            "GBS0441");

        Assert.Equal(GBSeverity.Info, reported.Severity);
        Assert.Contains("bank switches", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Banked code called only from resident code must never be told to join it.
    /// That would be advising the developer to undo the [Bank] they wrote, and
    /// bank 0 is the 16 KB banking exists to protect in the first place.
    /// </summary>
    [Fact]
    public void ACalleeCalledOnlyFromBankZeroIsNotToldToMoveThere() =>
        TestHarness.AssertNotReported(
            Diagnostics("        Level.Load();", """
                [Bank(2)]
                public static class Level
                {
                    public static void Load() { Sprites[0].X = 1; }
                }
                """),
            "GBS0441");

    /// <summary>
    /// Callers spread across banks have nowhere single to move the callee to, so
    /// there is no saving to report and nothing to say.
    /// </summary>
    [Fact]
    public void ACalleeCalledFromSeveralBanksIsNotReported() =>
        TestHarness.AssertNotReported(
            Diagnostics("        One.Run(); Two.Run();", """
                [Bank(1)]
                public static class One
                {
                    public static void Run() { Target.Work(); }
                }

                [Bank(3)]
                public static class Two
                {
                    public static void Run() { Target.Work(); }
                }

                [Bank(2)]
                public static class Target
                {
                    public static void Work() { Sprites[0].X = 1; }
                }
                """),
            "GBS0441");

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// The whole band in one line. This is what the category work before it was
    /// for: a band that can only be muted one id at a time gets muted wholesale.
    /// </summary>
    [Fact]
    public void TheWholeBandCanBeSilencedAtOnce()
    {
        foreach (GBDiagnosticDescriptor descriptor in GBDiagnostics.All
                     .Where(d => d.Category == GBDiagnosticCategory.CycleCost))
        {
            Assert.True(descriptor.IsSuppressible, descriptor.Id + " should be suppressible");
        }
    }

    /// <summary>
    /// Every one of these needs the whole lowered module: a call graph keyed on
    /// mangled names, resolved banks, inferred widths. An analyzer sees one syntax
    /// tree, so reporting them in the editor would produce squiggles a build could
    /// not reproduce, which is exactly what the parity test forbids.
    /// </summary>
    [Fact]
    public void NoCycleCostDiagnosticIsReportedInTheEditor()
    {
        Assert.DoesNotContain(
            GBSharp.Rules.GBRuleCatalog.IdeReportable,
            d => d.Category == GBDiagnosticCategory.CycleCost);
    }
}
