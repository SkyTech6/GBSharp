using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Tests.Diagnostics;

/// <summary>
/// Out-of-subset C# must fail with a GB# diagnostic that names an alternative,
/// at the right place in the developer's own source.
/// </summary>
/// <remarks>
/// Thesis section 6: unsupported features should fail with purpose-built GB#
/// diagnostics rather than leaking obscure backend errors. A test that only
/// checked "compilation failed" would pass for the exact failure mode this
/// project exists to avoid, so these assert the id and the message content.
/// </remarks>
public sealed class DiagnosticTests
{
    [Fact]
    public void ListRequiresDynamicAllocation()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;
            using System.Collections.Generic;

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                }
            }

            public static class State
            {
                public static List<byte> Items = new List<byte>();
            }
            """);

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0042");

        Assert.Equal(GBSeverity.Error, diagnostic.Severity);
        Assert.Contains("FixedList", diagnostic.Descriptor.Help);
    }

    [Fact]
    public void StringIsUnavailable()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                public static string Label = "hi";
            }
            """));

        TestHarness.AssertReported(diagnostics, "GBS0043");
    }

    [Fact]
    public void ExceptionsAreUnavailable()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program("""
                    try
                    {
                        Display.Enable();
                    }
                    catch (System.Exception)
                    {
                    }
            """));

        TestHarness.AssertReported(diagnostics, "GBS0044");
    }

    [Fact]
    public void ClassAllocationIsRejected()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Player p = new Player();",
            """
            public class Player
            {
                public byte X;
            }
            """));

        TestHarness.AssertReported(diagnostics, "GBS0050");
    }

    [Fact]
    public void ForeachIsRejectedWithItsOwnName()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            """
                    foreach (byte lane in State.Lanes)
                    {
                        Sprites.Hide(lane);
                    }
            """,
            """
            public static class State
            {
                public static byte[] Lanes = new byte[4];
            }
            """));

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0001");
        Assert.Contains("foreach", diagnostic.Message);
    }

    [Fact]
    public void MissingEntryPointIsReported()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public static class NotAProgram
            {
                public static void Start() { Display.Enable(); }
            }
            """);

        TestHarness.AssertReported(diagnostics, "GBS0003");
    }

    [Fact]
    public void FixedCollectionWithoutCapacityIsReported()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                public static FixedList<Enemy> Enemies;
            }

            public struct Enemy { public byte X; }
            """));

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0054");
        Assert.Contains("Capacity", diagnostic.Descriptor.Help);
    }

    [Fact]
    public void DiagnosticsPointAtTheOriginalCSharpLine()
    {
        // GB# reports against C#, never against generated C.
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;
            using static GB.Hardware;

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    int wide = Widen();
                    Sprites.Hide((byte)wide);
                }

                private static int Widen()
                {
                    byte a = 200;
                    byte b = 100;
                    return a + b;
                }
            }
            """);

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0007");

        Assert.EndsWith("Program.cs", diagnostic.Span.FilePath);
        Assert.True(diagnostic.Span.Line > 0, "diagnostic should carry a real source line");
    }

    [Fact]
    public void InvalidCSharpSurfacesRoslynsOwnError()
    {
        // If it is not valid C#, Roslyn's wording is the clearest answer and GB#
        // has nothing to add.
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public static class Program
            {
                public static void Main()
                {
                    this is not csharp
                }
            }
            """);

        Assert.Contains(diagnostics, d => d.Id.StartsWith("CS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Art.Tile[0] = 9;")]
    [InlineData("Art.Tile[0]++;")]
    [InlineData("Art.Tile[0] += 2;")]
    public void WritingToReadOnlyDataIsRejected(string write)
    {
        // C# allows this: readonly binds the reference, not the contents. GB#
        // puts the array in the cartridge, where the write cannot happen, so it
        // has to be caught here rather than as an SDCC error about generated C.
        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        " + write,
            """
            public static class Art
            {
                public static readonly byte[] Tile = { 1, 2, 3, 4 };
            }
            """));

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0056");
        Assert.Contains("Art.Tile", reported.Message);
    }

    [Fact]
    public void NonConstantDataElementsAreRejected()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static byte Seed = 3;
                public static readonly byte[] Tile = { 1, Seed, 3 };
            }
            """));

        TestHarness.AssertReported(diagnostics, "GBS0057");
    }

    [Fact]
    public void RomDataAndWramAreReportedSeparately()
    {
        // Reporting a tileset as WRAM would be worse than not reporting it: the
        // resource story is the point, so the two costs must not be conflated.
        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static readonly byte[] Tile = { 1, 2, 3, 4 };
            }
            """));

        GBDiagnostic rom = TestHarness.AssertReported(diagnostics, "GBS0203");
        Assert.Contains("4 bytes of ROM", rom.Message);
        TestHarness.AssertNotReported(diagnostics, "GBS0201");
    }

    [Fact]
    public void LinqIsUnavailable()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;
            using System.Linq;

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    Query.Run();
                }
            }

            public static class Query
            {
                private static readonly byte[] Items = { 1, 2, 3 };

                public static void Run()
                {
                    byte first = Items.First();
                }
            }
            """);

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0049");

        Assert.Equal(GBSeverity.Error, diagnostic.Severity);

        // The point of checking this ahead of the return type: the developer is
        // told LINQ is the problem, not that IEnumerable<byte> is.
        TestHarness.AssertNotReported(diagnostics, "GBS0002");
    }

    [Fact]
    public void LargeStructPassedByValueIsReported()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public struct Enemy
            {
                public byte X;
                public byte Y;
                public byte Health;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                }
            }

            public static class EnemySystem
            {
                public static void Update(Enemy enemy)
                {
                    enemy.X++;
                }
            }
            """);

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0202");

        Assert.Equal(GBSeverity.Resource, diagnostic.Severity);
        Assert.Contains("3 bytes", diagnostic.Message);
        Assert.Contains("ref", diagnostic.Descriptor.Help);
    }

    [Fact]
    public void StructPassedByRefIsNotReportedAsLarge()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public struct Enemy
            {
                public byte X;
                public byte Y;
                public byte Health;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                }
            }

            public static class EnemySystem
            {
                public static void Update(ref Enemy enemy)
                {
                    enemy.X++;
                }
            }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0202");
    }

    [Fact]
    public void EveryDiagnosticIdIsUniqueAndWellFormed()
    {
        var descriptors = typeof(GBDiagnostics)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(GBDiagnosticDescriptor))
            .Select(f => (GBDiagnosticDescriptor)f.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(descriptors);

        var duplicates = descriptors
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "duplicate diagnostic ids: " + string.Join(", ", duplicates));

        foreach (GBDiagnosticDescriptor descriptor in descriptors)
        {
            Assert.StartsWith("GBS", descriptor.Id);
            Assert.NotEmpty(descriptor.Title);
            Assert.NotEmpty(descriptor.MessageFormat);
        }
    }

    /// <summary>
    /// A constructor needs storage to construct into, and says so where it does not have any.
    /// </summary>
    /// <remarks>
    /// The alternative would be inventing a temporary, which is stack the
    /// developer never wrote and cannot see in the report, so the refusal is
    /// deliberate and the message names the two positions that do work.
    /// </remarks>
    [Fact]
    public void AConstructorInAnArgumentIsRefusedWithAWorkaround()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            """
                    Display.Enable();
                    byte n = Helpers.Take(new Point(1, 2));
            """,
            """
            public struct Point
            {
                public byte X;
                public byte Y;

                public Point(byte x, byte y)
                {
                    X = x;
                    Y = y;
                }
            }

            public static class Helpers
            {
                public static byte Take(Point p) => p.X;
            }
            """));

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0059");

        Assert.Equal(GBSeverity.Error, diagnostic.Severity);
        Assert.Contains("Point", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("new Point(3, 4)", diagnostic.Descriptor.Help ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two constructors on one struct would mangle to one C name.
    /// </summary>
    /// <remarks>
    /// Refused rather than renamed with a generated suffix, because a name a
    /// developer cannot find again in a linker map defeats the point of
    /// readable mangling (thesis section 3.3).
    /// </remarks>
    [Fact]
    public void OverloadedStructConstructorsAreRefused()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program(
            "        Display.Enable();",
            """
            public struct Point
            {
                public byte X;

                public Point(byte x)
                {
                    X = x;
                }

                public Point(byte x, byte y)
                {
                    X = (byte)(x + y);
                }
            }
            """));

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0001");
        Assert.Contains("more than one constructor", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The numeric band an id sits in has to match the category it declares.
    /// </summary>
    /// <remarks>
    /// The bands are the documented contract: a developer reading GBS0304 in a
    /// build log should know it is about banking without looking it up. Nothing
    /// enforced that until now, so an id could drift into the wrong band and the
    /// only cost would be a reader's confusion much later.
    /// <para>
    /// This checks the band against the <em>category</em> only, not the severity.
    /// GBS0007 is deliberately a Language-category diagnostic with Performance
    /// severity: 32-bit arithmetic is a fact about the subset that happens to be
    /// reported as a cost.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void EveryIdSitsInItsCategoryBand(string id, GBDiagnosticCategory category)
    {
        int number = int.Parse(id.AsSpan("GBS".Length), System.Globalization.CultureInfo.InvariantCulture);

        GBDiagnosticCategory expected = number switch
        {
            >= 1 and <= 99 => GBDiagnosticCategory.Language,
            >= 100 and <= 199 => GBDiagnosticCategory.Performance,
            >= 200 and <= 299 => GBDiagnosticCategory.Memory,
            >= 300 and <= 399 => GBDiagnosticCategory.Banking,
            >= 400 and <= 499 => GBDiagnosticCategory.CycleCost,
            >= 500 and <= 599 => GBDiagnosticCategory.Toolchain,
            >= 600 and <= 699 => GBDiagnosticCategory.Assets,
            >= 9000 => GBDiagnosticCategory.Internal,
            _ => throw new Xunit.Sdk.XunitException($"{id} is in no defined band."),
        };

        Assert.True(
            expected == category,
            $"{id} is in the {expected} band but declares category {category}.");
    }

    public static TheoryData<string, GBDiagnosticCategory> AllDescriptors()
    {
        var data = new TheoryData<string, GBDiagnosticCategory>();

        foreach (GBDiagnosticDescriptor descriptor in Descriptors())
        {
            data.Add(descriptor.Id, descriptor.Category);
        }

        return data;
    }

    private static IEnumerable<GBDiagnosticDescriptor> Descriptors() =>
        typeof(GBDiagnostics)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(GBDiagnosticDescriptor))
            .Select(f => (GBDiagnosticDescriptor)f.GetValue(null)!);

    /// <summary>
    /// A declared diagnostic nobody reports is a promise the compiler does not keep.
    /// </summary>
    /// <remarks>
    /// GBS0202 and GBS0049 were both declared and never reported. This is the
    /// check that would have caught them. Bands reserved for unimplemented
    /// phases are exempt by being empty: a descriptor that exists is expected
    /// to fire.
    /// </remarks>
    [Fact]
    public void EveryDeclaredDiagnosticIsReportedSomewhere()
    {
        string[] sources = Directory
            .EnumerateFiles(TestHarness.RepositoryRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.EndsWith("GBDiagnostics.cs", StringComparison.Ordinal))
            .ToArray();

        string allCode = string.Join("\n", sources.Select(File.ReadAllText));

        var unreported = new List<string>();

        foreach (System.Reflection.FieldInfo field in typeof(GBDiagnostics)
                     .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(GBDiagnosticDescriptor)))
        {
            if (!allCode.Contains($"GBDiagnostics.{field.Name}", StringComparison.Ordinal))
            {
                unreported.Add($"{((GBDiagnosticDescriptor)field.GetValue(null)!).Id} ({field.Name})");
            }
        }

        Assert.True(
            unreported.Count == 0,
            "declared but never referenced outside GBDiagnostics.cs: " + string.Join(", ", unreported));
    }
}
