using GBSharp.Compiler;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// Where <c>[Bank]</c> puts code and data, before any of it reaches GBDK.
/// </summary>
/// <remarks>
/// Placement is decided in the frontend and carried on the IR, so all of this is
/// testable without a toolchain. The regression that matters most is the one
/// asserting an unbanked program is unchanged: banking is meant to be additive,
/// and a program that never says <c>[Bank]</c> should compile to exactly what it
/// did before banking existed.
/// </remarks>
public sealed class BankingTests
{
    private const string BankedLevel = """
        using GB;

        [Bank(3)]
        public static class Level
        {
            public static readonly byte[] Art = { 1, 2, 3, 4 };

            public static byte Load() => Art[0];
        }

        public static class Program
        {
            public static void Main()
            {
                Display.Enable();
                byte first = Level.Load();
            }
        }
        """;

    private static IRFunction Function(IRModule module, string name) =>
        module.Functions.Single(f => f.Name == name);

    [Fact]
    public void AnExplicitBankLandsOnTheFunction()
    {
        IRModule module = TestHarness.CompileModule(BankedLevel);

        Assert.Equal(IRBank.Fixed(3), Function(module, "Level_Load").Bank);
    }

    [Fact]
    public void ReadOnlyDataInheritsItsTypesBank()
    {
        IRModule module = TestHarness.CompileModule(BankedLevel);

        IRGlobal art = module.Globals.Single(g => g.Name == "Level_Art");

        Assert.True(art.IsReadOnly);
        Assert.Equal(IRBank.Fixed(3), art.Bank);
    }

    [Fact]
    public void TheEntryPointStaysResident()
    {
        IRModule module = TestHarness.CompileModule(BankedLevel);

        Assert.True(module.EntryPoint.Bank.IsResident);
    }

    [Fact]
    public void AMembersOwnBankBeatsItsContainingTypes()
    {
        IRModule module = TestHarness.CompileModule("""
            using GB;

            [Bank(3)]
            public static class Level
            {
                public static byte Load() => 1;

                [Bank(0)]
                public static byte Tick() => 2;

                [Bank(5)]
                public static byte Music() => 3;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                    byte b = Level.Tick();
                    byte c = Level.Music();
                }
            }
            """);

        Assert.Equal(IRBank.Fixed(3), Function(module, "Level_Load").Bank);
        Assert.Equal(IRBank.Fixed(5), Function(module, "Level_Music").Bank);

        // [Bank(0)] is a request to stay mapped, not an absent attribute.
        Assert.True(Function(module, "Level_Tick").Bank.IsResident);
    }

    [Fact]
    public void BankWithoutANumberIsAutomatic()
    {
        IRModule module = TestHarness.CompileModule("""
            using GB;

            [Bank]
            public static class Level
            {
                public static byte Load() => 1;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                }
            }
            """);

        Assert.Equal(IRBankKind.Automatic, Function(module, "Level_Load").Bank.Kind);
    }

    /// <summary>
    /// The regression guard for the whole slice: banking is additive.
    /// </summary>
    [Fact]
    public void AnUnbankedProgramPrintsNoBankModifiers()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("""
                    Display.Enable();

                    byte x = 80;
                    Sprites.Move(0, x, 72);
            """));

        Assert.DoesNotContain("banked(", IRPrinter.Print(module), StringComparison.Ordinal);
    }

    [Fact]
    public void ABankedDeclarationIsPrinted()
    {
        string ir = IRPrinter.Print(TestHarness.CompileModule(BankedLevel));

        Assert.Contains("func banked(3) Level_Load(", ir, StringComparison.Ordinal);
        Assert.Contains("rom global banked(3) ", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticPlacementIsPrintedAsAuto()
    {
        string ir = IRPrinter.Print(TestHarness.CompileModule("""
            using GB;

            public static class Level
            {
                [Bank]
                public static byte Load() => 1;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                }
            }
            """));

        Assert.Contains("func banked(auto) Level_Load(", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void MutableStaticDataCannotBeBanked()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public static class State
            {
                [Bank(2)]
                public static byte Frame;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    State.Frame++;
                }
            }
            """);

        TestHarness.AssertReported(diagnostics, "GBS0306");
    }

    /// <summary>
    /// A banked class may still hold mutable state; only the field's own request
    /// is an error. Otherwise a class could not have both banked art and a counter.
    /// </summary>
    [Fact]
    public void MutableStateInABankedClassIsNotAnError()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            [Bank(2)]
            public static class Level
            {
                public static byte Frame;

                public static void Tick() { Frame++; }
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    Level.Tick();
                }
            }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0306");
    }

    [Theory]
    [InlineData(256)]
    [InlineData(-1)]
    public void ABankOutsideTheCartridgeIsRejected(int bank)
    {
        var diagnostics = TestHarness.DiagnosticsFor($$"""
            using GB;

            public static class Level
            {
                [Bank({{bank}})]
                public static byte Load() => 1;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                }
            }
            """);

        TestHarness.AssertReported(diagnostics, "GBS0304");
    }

    [Fact]
    public void TheEntryPointCannotAskForABank()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            public static class Program
            {
                [Bank(2)]
                public static void Main()
                {
                    Display.Enable();
                }
            }
            """);

        TestHarness.AssertReported(diagnostics, "GBS0300");
    }

    /// <summary>
    /// The cost is paid by the caller, so it is reported where the call is written.
    /// </summary>
    [Fact]
    public void ABankedCallIsReportedAtTheCallSite()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            [Bank(3)]
            public static class Level
            {
                public static byte Load() => 1;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                }
            }
            """);

        var diagnostic = TestHarness.AssertReported(diagnostics, "GBS0301");

        Assert.Equal(GBSeverity.Performance, diagnostic.Severity);
        Assert.Contains("bank 3", diagnostic.Message);

        // Line 14 is 'byte a = Level.Load();', not line 6 where Load is declared.
        Assert.Equal(14, diagnostic.Span.Line);
    }

    [Fact]
    public void ACallWithinTheSameBankIsNotReported()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            [Bank(3)]
            public static class Level
            {
                public static byte Load() => Helper();

                public static byte Helper() => 1;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.Load();
                }
            }
            """);

        // Main -> Load crosses into bank 3 and is reported once; Load -> Helper
        // stays inside bank 3 and costs nothing extra.
        Assert.Single(diagnostics, d => d.Id == "GBS0301");
    }

    [Fact]
    public void ResidentCodeCannotReadBankedDataDirectly()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            [Bank(3)]
            public static class Level
            {
                public static readonly byte[] Art = { 1, 2, 3, 4 };
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte first = Level.Art[0];
                }
            }
            """);

        var diagnostic = TestHarness.AssertReported(diagnostics, "GBS0303");
        Assert.Contains("bank 3", diagnostic.Message);
    }

    [Fact]
    public void CodeInTheSameBankReadsItsOwnDataNormally()
    {
        var diagnostics = TestHarness.DiagnosticsFor("""
            using GB;

            [Bank(3)]
            public static class Level
            {
                public static readonly byte[] Art = { 1, 2, 3, 4 };

                public static byte First() => Art[0];
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte first = Level.First();
                }
            }
            """);

        TestHarness.AssertNotReported(diagnostics, "GBS0303");
    }

    [Fact]
    public void DeclaredDataOverflowingABankIsReported()
    {
        string filler = string.Join(", ", Enumerable.Repeat("1", 9000));

        var diagnostics = TestHarness.DiagnosticsFor($$"""
            using GB;

            [Bank(1)]
            public static class Level
            {
                public static readonly byte[] A = { {{filler}} };
                public static readonly byte[] B = { {{filler}} };

                public static byte First() => A[0];
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Level.First();
                }
            }
            """);

        var diagnostic = TestHarness.AssertReported(diagnostics, "GBS0302");
        Assert.Contains("18000", diagnostic.Message);
    }

    /// <summary>
    /// Content-identical assets share one copy in ROM, and one copy has one bank.
    /// </summary>
    /// <remarks>
    /// Without this the second field's bank is silently discarded and its author
    /// is told the assets are shared, which reads like good news rather than like
    /// their placement having been overruled.
    /// </remarks>
    [Fact]
    public void SharedAssetsAskingForDifferentBanksConflict()
    {
        CompilationResult result = TestHarness.CompileWithAssets("""
            using GB;

            public static class Program
            {
                [Bank(1)]
                [Asset("art.png")]
                private static TileMap First;

                [Bank(2)]
                [Asset("art.png")]
                private static TileMap Second;

                public static void Main()
                {
                    Background.Load(First);
                    Background.Load(Second);
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = Assets.TestPng.Rgb(16, 8, (_, _) => new(255, 255, 255, 255)) });

        TestHarness.AssertReported(result.Diagnostics, "GBS0305");
    }

    [Fact]
    public void SharedAssetsAgreeingOnABankStillShare()
    {
        CompilationResult result = TestHarness.CompileWithAssets("""
            using GB;

            public static class Program
            {
                [Bank(1)]
                [Asset("art.png")]
                private static TileMap First;

                [Bank(1)]
                [Asset("art.png")]
                private static TileMap Second;

                public static void Main()
                {
                    Background.Load(First);
                    Background.Load(Second);
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = Assets.TestPng.Rgb(16, 8, (_, _) => new(255, 255, 255, 255)) });

        TestHarness.AssertNotReported(result.Diagnostics, "GBS0305");
        TestHarness.AssertReported(result.Diagnostics, "GBS0621");
    }

    /// <summary>
    /// Inheriting a bank is not asking for one, so a banked class containing Main
    /// is fine: Main stays resident and the rest of the class still moves.
    /// </summary>
    [Fact]
    public void ABankedClassMayContainTheEntryPoint()
    {
        CompilationResult result = TestHarness.Compile("""
            using GB;

            [Bank(2)]
            public static class Program
            {
                public static void Helper() { }

                public static void Main()
                {
                    Display.Enable();
                    Helper();
                }
            }
            """);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        TestHarness.AssertNotReported(result.Diagnostics, "GBS0300");

        Assert.True(result.Module!.EntryPoint.Bank.IsResident);
        Assert.Equal(IRBank.Fixed(2), Function(result.Module, "Program_Helper").Bank);
    }
}
