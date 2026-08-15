using GBSharp.Backend.GBDK;

namespace GBSharp.Tests.Backend;

/// <summary>
/// How banking reaches the generated C, without needing GBDK to say so.
/// </summary>
/// <remarks>
/// The emitter decides which file a declaration lands in and what qualifier it
/// carries, and both are visible in the text, so all of this is assertable
/// before a toolchain is involved.
/// </remarks>
public sealed class BankEmissionTests
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

    private static EmittedFile File(IReadOnlyList<EmittedFile> files, string name) =>
        files.Single(f => f.Name == name);

    /// <summary>
    /// The regression guard for the whole slice: banking must be additive.
    /// </summary>
    [Fact]
    public void ASingleBankProgramStillEmitsExactlyTwoFiles()
    {
        var files = TestHarness.EmitFiles(TestHarness.Program("""
                    Display.Enable();

                    byte x = 80;
                    Sprites.Move(0, x, 72);
            """));

        Assert.Equal(2, files.Count);
        Assert.Equal(CEmitter.HeaderFileName, files[0].Name);
        Assert.Equal(CEmitter.ProgramFileName, files[1].Name);
        Assert.All(files, f => Assert.Null(f.RomBank));
    }

    [Fact]
    public void ABankedDeclarationGetsItsOwnTranslationUnit()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        EmittedFile bank = File(files, "game_bank3.c");

        Assert.Equal(EmittedFileKind.TranslationUnit, bank.Kind);
        Assert.Equal(3, bank.RomBank);
    }

    /// <summary>
    /// First line, so the placement is answerable by opening the file.
    /// </summary>
    [Fact]
    public void ABankedFileDeclaresItsBankBeforeAnythingElse()
    {
        string text = File(TestHarness.EmitFiles(BankedLevel), "game_bank3.c").Text;

        string firstCode = text
            .Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Length > 0 && !l.StartsWith("/*", StringComparison.Ordinal)
                                     && !l.StartsWith("*", StringComparison.Ordinal));

        Assert.Equal("#pragma bank 3", firstCode);
    }

    [Fact]
    public void AutomaticPlacementUsesThePackerSentinel()
    {
        var files = TestHarness.EmitFiles("""
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

        EmittedFile bank = Assert.Single(files, f => f.Name.StartsWith("game_auto_", StringComparison.Ordinal));

        Assert.Equal(CEmitter.AutoBankSentinel, bank.RomBank);
        Assert.Contains("#pragma bank 255", bank.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// SDCC accepts a prototype and definition that disagree about BANKED and
    /// miscompiles the call, so both halves are asserted rather than one.
    /// </summary>
    [Fact]
    public void ThePrototypeAndTheDefinitionBothCarryTheQualifier()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        Assert.Contains("uint8_t Level_Load(void) BANKED;", File(files, CEmitter.HeaderFileName).Text, StringComparison.Ordinal);
        Assert.Contains("uint8_t Level_Load(void) BANKED\n", File(files, "game_bank3.c").Text.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void ResidentFunctionsCarryNoQualifier()
    {
        string header = File(TestHarness.EmitFiles(BankedLevel), CEmitter.HeaderFileName).Text;

        Assert.Contains("void Program_Main(void);", header, StringComparison.Ordinal);
    }

    [Fact]
    public void BankedDataGetsItsCompanionBankSymbol()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        Assert.Contains("BANKREF(Level_Art)", File(files, "game_bank3.c").Text, StringComparison.Ordinal);
        Assert.Contains("BANKREF_EXTERN(Level_Art)", File(files, CEmitter.HeaderFileName).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BankedDataSaysWhichBankItIsIn()
    {
        string bank = File(TestHarness.EmitFiles(BankedLevel), "game_bank3.c").Text;

        // The comment continues with the declaring C# location, so this asserts
        // the part that is about banking rather than pinning the whole line.
        Assert.Contains("/* 4 bytes, ROM bank 3", bank, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reader of the generated C should not have to guess which declaration
    /// produced a definition.
    /// </summary>
    [Fact]
    public void GeneratedDeclarationsNameTheirCSharpOrigin()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        string bank = File(files, "game_bank3.c").Text;

        Assert.Contains("Level.Load()", bank, StringComparison.Ordinal);
        Assert.Contains("Program.cs(", bank, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partitioning bug that moved main would present as a ROM that does not
    /// boot, which is an expensive way to find out. One line catches it.
    /// </summary>
    [Fact]
    public void MainAndTheResidentCodeStayInTheProgramFile()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        string program = File(files, CEmitter.ProgramFileName).Text;
        string bank = File(files, "game_bank3.c").Text;

        Assert.Contains("void main(void)", program, StringComparison.Ordinal);
        Assert.Contains("void Program_Main(void)", program, StringComparison.Ordinal);

        Assert.DoesNotContain("void main(void)", bank, StringComparison.Ordinal);
        Assert.DoesNotContain("Program_Main", bank, StringComparison.Ordinal);
    }

    [Fact]
    public void BankedDeclarationsAreDefinedOnlyOnce()
    {
        var files = TestHarness.EmitFiles(BankedLevel);

        string program = File(files, CEmitter.ProgramFileName).Text;

        // The definitions moved out; only the header's extern declaration remains.
        Assert.DoesNotContain("Level_Art[4] =", program, StringComparison.Ordinal);
        Assert.DoesNotContain("uint8_t Level_Load(void)", program, StringComparison.Ordinal);
    }

    [Fact]
    public void EachExplicitBankGetsOneFile()
    {
        var files = TestHarness.EmitFiles("""
            using GB;

            [Bank(1)]
            public static class Levels
            {
                public static byte One() => 1;
                public static byte Two() => 2;
            }

            [Bank(2)]
            public static class Music
            {
                public static byte Play() => 3;
            }

            public static class Program
            {
                public static void Main()
                {
                    Display.Enable();
                    byte a = Levels.One();
                    byte b = Levels.Two();
                    byte c = Music.Play();
                }
            }
            """);

        EmittedFile one = File(files, "game_bank1.c");

        Assert.Contains("Levels_One", one.Text, StringComparison.Ordinal);
        Assert.Contains("Levels_Two", one.Text, StringComparison.Ordinal);
        Assert.Contains("Music_Play", File(files, "game_bank2.c").Text, StringComparison.Ordinal);
    }
}
