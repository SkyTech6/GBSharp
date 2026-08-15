using GBSharp.Compiler.Diagnostics;
using GBSharp.Rules;

namespace GBSharp.Tests.Analyzers;

/// <summary>
/// The contract between the editor and the build.
/// </summary>
/// <remarks>
/// <para>
/// A GBS id has to mean one thing. The analyzer and the compiler share their
/// rules through <see cref="SubsetRules"/> so they cannot disagree about a
/// verdict, but sharing the rule is not the same as agreeing about which rules
/// each side runs: that is what these assert.
/// </para>
/// <para>
/// The direction matters. The analyzer reporting less than a build is fine and
/// expected: it never touches disk, so the asset and toolchain bands are absent
/// by design. The analyzer reporting something a build does not is a bug, and it
/// is the bug that would erode trust fastest: a red squiggle over code that
/// compiles.
/// </para>
/// </remarks>
public sealed class AnalyzerParityTests
{
    /// <summary>Programs that break the subset in the ways the analyzer covers.</summary>
    public static TheoryData<string, string> BadPrograms() => new()
    {
        {
            "GBS0042", """
            using GB;
            using System.Collections.Generic;

            public static class Program
            {
                public static List<byte> Items = new List<byte>();

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0043", """
            using GB;

            public static class Program
            {
                public static string Name;

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0002", """
            using GB;

            public static class Program
            {
                public static double Ratio;

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0046", """
            using GB;

            public interface IThing { }

            public static class Program
            {
                public static IThing Thing;

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0045", """
            using GB;
            using System;

            public static class Program
            {
                public static Action Callback;

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0050", """
            using GB;

            public class Thing { }

            public static class Program
            {
                public static Thing Instance;

                public static void Main() => Display.Enable();
            }
            """
        },
        {
            "GBS0049", """
            using GB;
            using System.Linq;

            public static class Program
            {
                private static readonly byte[] Items = { 1, 2, 3 };

                public static void Main()
                {
                    Display.Enable();
                    byte first = Items.First();
                }
            }
            """
        },
    };

    /// <summary>
    /// The id the analyzer is meant to catch is the one a build reports too.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadPrograms))]
    public void TheAnalyzerAndTheCompilerAgreeOnTheId(string expected, string source)
    {
        var compiled = TestHarness.DiagnosticsFor(source);
        var analyzed = TestHarness.Analyze(source);

        TestHarness.AssertReported(compiled, expected);

        Assert.True(
            analyzed.Any(d => d.Id == expected),
            $"the analyzer did not report {expected}; it reported: " +
            (analyzed.Count == 0 ? "(nothing)" : string.Join(", ", analyzed.Select(d => d.Id))));
    }

    /// <summary>
    /// The direction that must never break: nothing the analyzer says is absent
    /// from a real build.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadPrograms))]
    public void TheAnalyzerNeverReportsWhatABuildWouldNot(string expected, string source)
    {
        _ = expected;

        var compiled = TestHarness.DiagnosticsFor(source).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var analyzed = TestHarness.Analyze(source).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        analyzed.ExceptWith(compiled);

        Assert.True(
            analyzed.Count == 0,
            "the analyzer reported ids a build does not: " + string.Join(", ", analyzed.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// A program the compiler accepts must be quiet in the editor too.
    /// </summary>
    [Fact]
    public void ValidCodeProducesNoAnalyzerErrors()
    {
        var analyzed = TestHarness.Analyze(TestHarness.Program("""
                    Display.Enable();

                    byte x = 80;

                    while (true)
                    {
                        if (Input.Right)
                            x++;

                        Sprites.Move(0, x, 72);
                        Game.WaitVBlank();
                    }
            """));

        Assert.DoesNotContain(
            analyzed,
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    /// <summary>
    /// An attribute application's implicit constructor call must not be
    /// mistaken for a heap allocation.
    /// </summary>
    /// <remarks>
    /// Roslyn models the constructor call behind <c>[Asset("tiles.png")]</c> as
    /// an <c>IObjectCreationOperation</c> too, the same operation kind a real
    /// <c>new Thing()</c> raises. The compiler never routes an attribute
    /// through <c>ClassifyType</c> (asset fields are claimed by
    /// <c>AssetBindings</c> before that path runs), so the analyzer flagging one
    /// would be exactly the red squiggle over compiling code that
    /// <see cref="TheAnalyzerNeverReportsWhatABuildWouldNot"/> exists to catch.
    /// </remarks>
    [Fact]
    public void AttributeArgumentsDoNotTriggerReferenceTypeAllocation()
    {
        var analyzed = TestHarness.Analyze("""
            using GB;

            public static class Program
            {
                [Asset("tiles.png")]
                private static TileMap Art;

                public static void Main() => Display.Enable();
            }
            """);

        Assert.DoesNotContain(analyzed, d => d.Id == "GBS0050");
    }

    /// <summary>
    /// The fix for the attribute false positive must not blind the analyzer to
    /// a real heap allocation, which arrives through the very same operation
    /// kind.
    /// </summary>
    [Fact]
    public void RealObjectCreationStillTriggersReferenceTypeAllocation()
    {
        var analyzed = TestHarness.Analyze("""
            using GB;

            public class Thing { }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    Thing t = new Thing();
                }
            }
            """);

        Assert.Contains(analyzed, d => d.Id == "GBS0050");
    }

    /// <summary>
    /// The catalog is the analyzer's declaration of what it covers, so it cannot
    /// be allowed to drift from what the analyzer actually supports.
    /// </summary>
    [Fact]
    public void EveryCatalogedRuleIsSupportedByTheAnalyzer()
    {
        var supported = new GBSharp.Analyzers.GBSubsetAnalyzer()
            .SupportedDiagnostics
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (GBDiagnosticDescriptor descriptor in GBRuleCatalog.IdeReportable)
        {
            Assert.Contains(descriptor.Id, supported);
        }
    }

    /// <summary>
    /// Performance and resource notes must not arrive as warnings: they are
    /// always-on and unfixable, so a project with warnings-as-errors would stop
    /// building the moment it declared a static field.
    /// </summary>
    [Fact]
    public void CostNotesAreInformationalNotWarnings()
    {
        foreach (GBDiagnosticDescriptor descriptor in GBRuleCatalog.IdeReportable)
        {
            if (descriptor.DefaultSeverity is not (GBSeverity.Performance or GBSeverity.Resource))
            {
                continue;
            }

            var roslyn = GBRuleCatalog.ToRoslyn(descriptor);

            Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, roslyn.DefaultSeverity);

            // The distinction Roslyn's severity cannot carry lives in the
            // category, which is what .editorconfig can actually filter on.
            Assert.StartsWith("GBSharp.", roslyn.Category, StringComparison.Ordinal);
        }
    }
}
