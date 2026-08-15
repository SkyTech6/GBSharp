using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;

namespace GBSharp.Tests.Backend;

/// <summary>
/// The generated C. These run without GBDK.
/// </summary>
/// <remarks>
/// The assertions target the properties that make the output usable: real
/// control flow, original names, correct declaration order, rather than
/// whole-file golden text. A golden file would fail on every cosmetic change
/// and say nothing about which property broke.
/// </remarks>
public sealed class CEmitterTests
{
    [Fact]
    public void ControlFlowIsEmittedAsControlFlowNotGotos()
    {
        // The whole reason the IR is structured rather than a flattened CFG.
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte i = 0;
                    while (i < 10)
                    {
                        if (Input.Right)
                        {
                            i++;
                        }
                    }
            """));

        Assert.Contains("while (", c);
        Assert.Contains("if (", c);
        Assert.DoesNotContain("goto", c);
    }

    [Fact]
    public void FunctionNamesAreReadable()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Enemies.Update();",
            """
            public static class Enemies
            {
                public static void Update() { }
            }
            """));

        Assert.Contains("void Enemies_Update(void)", c);
    }

    [Fact]
    public void StructInstanceMethodsTakeASelfPointer()
    {
        // The shape thesis section 9 specifies: Player_Update(Player* self).
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public struct Player
            {
                public byte X;

                public void Update()
                {
                    X++;
                }
            }
            """));

        Assert.Contains("void Player_Update(Player* self)", c);
        Assert.Contains("self->X++;", c);
    }

    /// <summary>
    /// The other half of the shape above, which went unasserted and was wrong.
    /// </summary>
    /// <remarks>
    /// The declaration was always right; the call site passed the struct by
    /// value, so SDCC rejected code GB# had emitted. A test that checks only the
    /// declaration passes for that bug, which is why this one exists and why it
    /// asserts against every receiver kind rather than the convenient one.
    /// </remarks>
    [Fact]
    public void CallingAStructInstanceMethodPassesTheReceiverByAddress()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Player local;
                    local.X = 1;
                    local.Update();
                    Helpers.Field.Update();
                    Helpers.ByRef(ref Helpers.Field);
            """,
            """
            public struct Player
            {
                public byte X;

                public void Update()
                {
                    X++;
                }
            }

            public static class Helpers
            {
                public static Player Field;

                public static void ByRef(ref Player p)
                {
                    p.Update();
                }
            }
            """));

        // A local and a global are addressed; a 'ref' parameter is already a
        // pointer and must be handed straight on rather than becoming '&(*p)'.
        Assert.Contains("Player_Update((&local));", c);
        Assert.Contains("Player_Update((&Helpers_Field));", c);
        Assert.Contains("Player_Update(p);", c);
        Assert.DoesNotContain("&(*", c);
    }

    [Fact]
    public void ReadingAStructPropertyPassesTheReceiverByAddress()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Player local;
                    local.X = 1;
                    byte d = local.Doubled;
            """,
            """
            public struct Player
            {
                public byte X;

                public byte Doubled => (byte)(X + X);
            }
            """));

        Assert.Contains("uint8_t Player_get_Doubled(Player* self)", c);
        Assert.Contains("Player_get_Doubled((&local))", c);
    }

    /// <summary>
    /// Writing a property has to call its setter.
    /// </summary>
    /// <remarks>
    /// It used to lower the target as a <em>read</em> and emit
    /// <c>Program_get_Counter() = 9U;</c>, leaving the emitted setter as dead
    /// code. C rejects that, but only after GB# had already handed it over, so
    /// the assertion that matters is that the setter is called, not merely that
    /// something was emitted.
    /// </remarks>
    [Fact]
    public void WritingAUserPropertyCallsTheSetter()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Counters.Value = 9;
                    Counters.Value += 2;
                    Counters.Value++;
                    Counters.Value--;
            """,
            """
            public static class Counters
            {
                private static byte storage;

                public static byte Value
                {
                    get { return storage; }
                    set { storage = value; }
                }
            }
            """));

        Assert.Contains("Counters_set_Value(9U);", c);

        // Read-modify-write has nothing to modify in place, so it becomes a get,
        // an operation and a set. The step prints with the width's suffix.
        Assert.Contains("Counters_set_Value((Counters_get_Value() + 2U));", c);
        Assert.Contains("Counters_set_Value((Counters_get_Value() + 1U));", c);
        Assert.Contains("Counters_set_Value((Counters_get_Value() - 1U));", c);

        // The bug this replaces: a getter call standing where an lvalue belongs.
        Assert.DoesNotContain("Counters_get_Value() =", c);
    }

    [Fact]
    public void AStructConstructorIsAFunctionTakingTheStorageItFills()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Point p = new Point(3, 4);
                    Origin.Value = new Point(10, 20);
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

            public static class Origin
            {
                public static Point Value;
            }
            """));

        Assert.Contains("void Point__ctor(Point* self, uint8_t x, uint8_t y)", c);
        Assert.Contains("self->X = x;", c);

        // Nothing is inlined and no temporary appears: a constructor is one
        // visible call against storage that already exists.
        Assert.Contains("Point__ctor((&p), 3U, 4U);", c);
        Assert.Contains("Point__ctor((&Origin_Value), 10U, 20U);", c);
    }

    [Fact]
    public void StructsAreDeclaredBeforeTheStructsThatContainThem()
    {
        // C needs a complete type before embedding it by value.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                [Capacity(4)]
                public static FixedList<Enemy> Enemies;
            }

            public struct Enemy
            {
                public byte X;
                public byte Y;
            }
            """));

        int enemy = c.IndexOf("typedef struct Enemy {", StringComparison.Ordinal);
        int list = c.IndexOf("typedef struct FixedList_Enemy_4 {", StringComparison.Ordinal);

        Assert.True(enemy >= 0, "Enemy struct should be emitted");
        Assert.True(list >= 0, "the specialised list struct should be emitted");
        Assert.True(enemy < list, "Enemy must be declared before the list that embeds it");
    }

    [Fact]
    public void FixedListSpecializesPerCapacity()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                [Capacity(4)]
                public static FixedList<Enemy> Small;

                [Capacity(16)]
                public static FixedList<Enemy> Large;
            }

            public struct Enemy { public byte X; }
            """));

        // Two capacities of the same element type are two distinct C types.
        Assert.Contains("typedef struct FixedList_Enemy_4 {", c);
        Assert.Contains("typedef struct FixedList_Enemy_16 {", c);
        Assert.Contains("Enemy items[4];", c);
        Assert.Contains("Enemy items[16];", c);
    }

    [Fact]
    public void FixedListOperationsAreEmittedAsOrdinaryFunctions()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Enemy e = new Enemy();
                    State.Enemies.Add(e);
                    State.Enemies.Clear();
            """,
            """
            public static class State
            {
                [Capacity(4)]
                public static FixedList<Enemy> Enemies;
            }

            public struct Enemy { public byte X; }
            """));

        Assert.Contains("uint8_t FixedList_Enemy_4_Add(FixedList_Enemy_4* self, Enemy item)", c);
        Assert.Contains("FixedList_Enemy_4_Add((&State_Enemies), e)", c);
        Assert.Contains("FixedList_Enemy_4_Clear((&State_Enemies))", c);
    }

    [Fact]
    public void CountAndCapacityLowerToAFieldAndAConstant()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    byte n = State.Enemies.Count;
                    byte cap = State.Enemies.Capacity;
                    Sprites.Hide((byte)(n + cap));
            """,
            """
            public static class State
            {
                [Capacity(4)]
                public static FixedList<Enemy> Enemies;
            }

            public struct Enemy { public byte X; }
            """));

        Assert.Contains("n = State_Enemies.count;", c);

        // Capacity is known at compile time, so it must not become a load.
        Assert.Contains("cap = 4U;", c);
    }

    [Fact]
    public void OnlyZeroInitializedStructsGetAZeroInstance()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    Used u = new Used();
                    Sprites.Hide(u.X);
            """,
            """
            public struct Used { public byte X; }
            public struct Unused { public byte X; }
            """));

        // Defined once and declared in the header, rather than 'static' in each
        // unit, which would duplicate the bytes in every file that included it.
        Assert.Contains("const Used Used__zero", c);
        Assert.Contains("extern const Used Used__zero;", c);
        Assert.DoesNotContain("static const Used Used__zero", c);
        Assert.DoesNotContain("Unused__zero", c);
    }

    [Fact]
    public void SpriteIndexerErasesToASingleOamWrite()
    {
        // The thesis section 23 line. The whole handle chain must vanish.
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte x = 80;
                    Sprites[0].X = x;
            """));

        Assert.Contains("gbs_sprite_set_x(0U, x);", c);
        Assert.DoesNotContain("SpriteRef", c);
        Assert.DoesNotContain("SpriteTable", c);
    }

    [Fact]
    public void UserCodeCanDeclareItsOwnNativeCalls()
    {
        // The escape hatch of thesis section 19. The framework and user code use
        // the same mechanism, so reaching GBDK directly needs no compiler change.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Raw.SetBackgroundTile(3, 4, 7);",
            """
            public static class Raw
            {
                [Native("set_bkg_tile_xy")]
                public static void SetBackgroundTile(byte x, byte y, byte tile)
                    => throw new System.NotSupportedException();
            }
            """));

        Assert.Contains("set_bkg_tile_xy(3U, 4U, 7U);", c);

        // The declaration itself must not be emitted; only the call survives.
        Assert.DoesNotContain("Raw_SetBackgroundTile", c);
    }

    [Fact]
    public void EntryPointIsWrappedInMain()
    {
        string c = TestHarness.EmitC(TestHarness.Program("        Display.Enable();"));

        Assert.Contains("void main(void)", c);
        Assert.Contains("Program_Main();", c);
    }

    [Fact]
    public void GeneratedFileIncludesTheRuntimeShim()
    {
        string c = TestHarness.EmitC(TestHarness.Program("        Display.Enable();"));

        Assert.Contains("#include <gb/gb.h>", c);
        Assert.Contains("#include \"gbs_runtime.h\"", c);
    }

    [Fact]
    public void SiblingBlockLocalsWithTheSameNameGetDistinctCNames()
    {
        // C# scopes locals to their block; the emitter hoists them to function
        // scope. Two sibling 'for (byte x ...)' loops used to emit
        // 'uint8_t x;' twice, which SDCC rejects as a duplicate symbol.
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    byte total = 0;
                    for (byte x = 0; x < 4; x++) { total += x; }
                    for (byte x = 0; x < 6; x++) { total += x; }
                    Sprites.SetTile(0, total);
            """));

        Assert.Contains("uint8_t x;", c);
        Assert.Contains("uint8_t x_2;", c);
        Assert.Contains("for (x_2 = 0U;", c);
    }

    [Fact]
    public void UserIncludesAreEmittedIntoTheSharedHeader()
    {
        // "includes" exists for the [Native] escape hatch: SDCC rejects a call
        // to an undeclared function, and the emitter cannot know a foreign
        // symbol's signature, so the developer supplies a header of their own.
        var emitter = new CEmitter(userIncludes: ["my_driver.h"]);
        IReadOnlyList<EmittedFile> files = emitter.Emit(
            TestHarness.CompileModule(TestHarness.Program("        Display.Enable();")));

        EmittedFile header = Assert.Single(files, f => f.Kind == EmittedFileKind.Header);
        Assert.Contains("#include \"my_driver.h\"", header.Text);

        // After the runtime shim, so the user header can rely on everything the
        // generated C already sees.
        Assert.True(
            header.Text.IndexOf("#include \"gbs_runtime.h\"", StringComparison.Ordinal) <
            header.Text.IndexOf("#include \"my_driver.h\"", StringComparison.Ordinal),
            "the user include should follow the runtime shim include");
    }

    [Fact]
    public void DeclarationsGoToAHeaderAndDefinitionsToTheUnit()
    {
        IReadOnlyList<EmittedFile> files = TestHarness.EmitFiles(TestHarness.Program(
            "        Helper.Bump();",
            """
            public static class Helper
            {
                public static byte Count;
                public static void Bump() { Count++; }
            }
            """));

        EmittedFile header = Assert.Single(files, f => f.Kind == EmittedFileKind.Header);
        EmittedFile unit = Assert.Single(files, f => f.Kind == EmittedFileKind.TranslationUnit);

        Assert.Equal("game.h", header.Name);
        Assert.Equal("game.c", unit.Name);

        // The header declares; it must never define, or a second translation
        // unit including it would collide with the first.
        Assert.Contains("extern uint8_t Helper_Count;", header.Text);
        Assert.Contains("void Helper_Bump(void);", header.Text);
        Assert.DoesNotContain("void main(void)\n{", header.Text);
        Assert.DoesNotContain(" = ", header.Text);

        // The unit defines, and reaches the declarations only through the header.
        Assert.Contains("#include \"game.h\"", unit.Text);
        Assert.Contains("uint8_t Helper_Count;", unit.Text);
        Assert.Contains("void main(void)", unit.Text);
    }

    [Fact]
    public void ReadOnlyDataIsConstSoItLandsInRom()
    {
        // 'const' is what puts the bytes in the cartridge. Without it a tileset
        // would be charged against the 8 KB of work RAM.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static readonly byte[] Tile = { 1, 2, 3, 4 };
                public static byte Frame;
            }
            """));

        Assert.Contains("const uint8_t Art_Tile[4] = { 1U, 2U, 3U, 4U };", c);
        Assert.DoesNotContain("const uint8_t Art_Frame", c);
    }

    [Fact]
    public void ArrayArgumentsPassWithoutACast()
    {
        // An array already decays to a pointer in C. A cast here would also
        // strip the const off ROM data on its way into a native call.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Raw.SetBackgroundData(0, 1, Art.Tile);",
            """
            public static class Art
            {
                public static readonly byte[] Tile = { 1, 2, 3, 4 };
            }

            public static class Raw
            {
                [Native("set_bkg_data")]
                public static void SetBackgroundData(byte first, byte count, byte[] data)
                    => throw new System.NotSupportedException();
            }
            """));

        Assert.Contains("set_bkg_data(0U, 1U, Art_Tile);", c);
        Assert.DoesNotContain("(uint8_t*)Art_Tile", c);
    }

    [Fact]
    public void ArrayParametersEmitAsPointers()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Helper
            {
                public static byte First(byte[] data) => data[0];
            }
            """));

        Assert.Contains("uint8_t Helper_First(uint8_t* data)", c);

        // 'uint8_t data[0]' is not valid C, and indexing must not read through.
        Assert.DoesNotContain("data[0]", c[..c.IndexOf("Helper_First(uint8_t* data)\n{", StringComparison.Ordinal)]);
        Assert.DoesNotContain("(*data)", c);
    }

    [Fact]
    public void LongDataWrapsSoItStaysReadable()
    {
        // Tile data is the main thing that ends up in an initializer list, and
        // a tileset on one line would be thousands of characters wide.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static readonly byte[] Tile =
                {
                    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
                    16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
                };
            }
            """));

        Assert.Contains("const uint8_t Art_Tile[32] = {\n", c);
        Assert.All(
            c.Split('\n'),
            line => Assert.True(line.Length < 100, $"line too wide to read: {line}"));
    }

    // -----------------------------------------------------------------------
    // --annotate-source
    // -----------------------------------------------------------------------

    /// <summary>Touches every statement kind CEmitter knows how to annotate.</summary>
    private const string AnnotationSample = """
                byte i = 0;
                while (i < 3)
                {
                    if (Input.Right)
                    {
                        i++;
                    }
                    else
                    {
                        i--;
                    }
                }

                Helper.Scan();
        """;

    private const string AnnotationHelpers = """
        public static class Helper
        {
            public static void Scan()
            {
                for (byte i = 0; i < 4; i++)
                {
                    switch (i)
                    {
                        case 0:
                            continue;
                        case 1:
                            break;
                        default:
                            return;
                    }
                }
            }
        }
        """;

    [Fact]
    public void AnnotateSourceOffLeavesDefaultOutputUnchanged()
    {
        // The default path must not shift by a single byte: this is the same
        // program every other test in this file compiles, through the same
        // EmitC an existing test would call, just with the flag spelled out.
        string program = TestHarness.Program(AnnotationSample, AnnotationHelpers);

        string defaultOutput = TestHarness.EmitC(program);
        string explicitlyOff = TestHarness.EmitC(program, annotateSource: false);

        Assert.Equal(defaultOutput, explicitlyOff);
        Assert.DoesNotContain("   /* - Program.cs(", defaultOutput);
    }

    [Fact]
    public void AnnotateSourceAddsAPerStatementCommentAndNothingElseChanges()
    {
        string program = TestHarness.Program(AnnotationSample, AnnotationHelpers);

        string plain = TestHarness.EmitC(program);
        string annotated = TestHarness.EmitC(program, annotateSource: true);

        Assert.NotEqual(plain, annotated);

        // One representative line per statement kind CEmitter can annotate.
        Assert.Contains("i = 0U;   /* - Program.cs(", annotated);
        Assert.Contains("while ((i < 3U))   /* - Program.cs(", annotated);
        Assert.Contains("if (gbs_input_right())   /* - Program.cs(", annotated);
        Assert.Contains("i++;   /* - Program.cs(", annotated);
        Assert.Contains("i--;   /* - Program.cs(", annotated);
        Assert.Contains("Helper_Scan();   /* - Program.cs(", annotated);
        Assert.Contains("switch (i)   /* - Program.cs(", annotated);
        Assert.Contains("continue;   /* - Program.cs(", annotated);
        Assert.Contains("break;   /* - Program.cs(", annotated);
        Assert.Contains("return;   /* - Program.cs(", annotated);

        // A block is a container, not a line of code, so its opening brace
        // never gets one.
        Assert.DoesNotContain("{   /* - Program.cs(", annotated);

        // Stripping every annotation should recover exactly the unannotated
        // output: the flag adds text, it does not restructure anything.
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            annotated, @"   /\* - Program\.cs\(\d+\) \*/", string.Empty);
        Assert.Equal(plain, stripped);
    }

    [Fact]
    public void AnnotateSourceSourceMapMatchesTheEmittedComments()
    {
        string program = TestHarness.Program(AnnotationSample, AnnotationHelpers);
        (IReadOnlyList<EmittedFile> files, IReadOnlyList<SourceMapEntry> sourceMap) =
            TestHarness.EmitAnnotated(program);

        Assert.NotEmpty(sourceMap);

        foreach (SourceMapEntry entry in sourceMap)
        {
            Assert.EndsWith("Program.cs", entry.File);
            Assert.True(entry.Line > 0, "a mapped statement must have a real C# line");

            EmittedFile generated = Assert.Single(files, f => f.Name == entry.GeneratedFile);
            string[] lines = generated.Text.Split('\n');

            Assert.InRange(entry.GeneratedLine, 1, lines.Length);

            // The row in sourcemap.json and the comment CEmitter wrote inline
            // must never be able to disagree about which line they mean.
            Assert.Contains($"Program.cs({entry.Line})", lines[entry.GeneratedLine - 1]);
        }
    }
}
