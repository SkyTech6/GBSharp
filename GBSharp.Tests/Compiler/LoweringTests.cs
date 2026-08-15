using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// C# in, IR out. These run without GBDK.
/// </summary>
public sealed class LoweringTests
{
    [Fact]
    public void EntryPointIsFound()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("        Display.Enable();"));

        Assert.Equal("Program_Main", module.EntryPoint.Name);
        Assert.Equal(IRPrimitiveType.Void, module.EntryPoint.ReturnType);
    }

    [Fact]
    public void NativeMembersLowerToTheirCSymbol()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("        Display.Enable();"));

        string ir = IRPrinter.Print(module);
        Assert.Contains("native gbs_display_on()", ir);
    }

    [Fact]
    public void StructuredControlFlowSurvivesLowering()
    {
        // The IR keeps loops as loops rather than flattening to branches; this
        // is what lets the backend emit readable C.
        IRModule module = TestHarness.CompileModule(TestHarness.Program("""
                    byte i = 0;
                    while (i < 10)
                    {
                        i++;
                    }
            """));

        IRStatement body = Assert.IsType<IRBlock>(module.EntryPoint.Body).Statements
            .First(s => s is IRWhile);

        var loop = Assert.IsType<IRWhile>(body);
        Assert.IsType<IRBlock>(loop.Body);
    }

    [Fact]
    public void ForLoopLowersToAForLoop()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("""
                    for (byte i = 0; i < 4; i++)
                    {
                        Sprites.Hide(i);
                    }
            """));

        Assert.Contains("for (", IRPrinter.Print(module));
    }

    [Fact]
    public void SwitchLowersToASwitch()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("""
                    byte kind = 1;
                    switch (kind)
                    {
                        case 0: Sprites.Hide(0); break;
                        case 1: Sprites.Hide(1); break;
                        default: break;
                    }
            """));

        string ir = IRPrinter.Print(module);
        Assert.Contains("switch", ir);
        Assert.Contains("default:", ir);
    }

    [Fact]
    public void StructFieldsProduceAStructWithTheRightSize()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public struct Point
            {
                public byte X;
                public byte Y;
                public ushort Distance;
            }
            """));

        IRStruct point = Assert.Single(module.Structs, s => s.Name == "Point");
        Assert.Equal(4, point.SizeInBytes);
    }

    [Fact]
    public void RefParametersBecomePointers()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public struct Point { public byte X; }

            public static class Mover
            {
                public static void Nudge(ref Point p) { p.X++; }
            }
            """));

        IRFunction nudge = Assert.Single(module.Functions, f => f.Name == "Mover_Nudge");
        Assert.IsType<IRPointerType>(Assert.Single(nudge.Parameters).Type);
    }

    [Fact]
    public void StaticFieldsBecomeGlobalsWithKnownSize()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                public static byte Frame;
                public static ushort Score;
            }
            """));

        Assert.Equal(3, module.Globals.Sum(g => g.Type.SizeInBytes));
    }

    [Fact]
    public void ArrayLengthComesFromTheDeclaration()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                public static byte[] Lanes = new byte[6];
            }
            """));

        IRGlobal lanes = Assert.Single(module.Globals);
        IRArrayType array = Assert.IsType<IRArrayType>(lanes.Type);

        Assert.Equal(6, array.Length);
        Assert.Equal(6, array.SizeInBytes);
    }

    [Fact]
    public void EnumMembersFoldToConstants()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program("""
                    byte b = (byte)Button.Start;
                    Sprites.Hide(b);
            """));

        // Button.Start is 0x80; it should appear as a constant, not a lookup.
        Assert.Contains("128", IRPrinter.Print(module));
    }

    [Fact]
    public void ReadOnlyArrayDataIsCapturedAndMarkedForRom()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static readonly byte[] Tile = { 1, 2, 3, 4 };
            }
            """));

        IRGlobal tile = Assert.Single(module.Globals);

        Assert.True(tile.IsReadOnly);

        // Without the aggregate the data would vanish and have to be written
        // element by element at runtime, which for a tileset is not possible.
        var aggregate = Assert.IsType<IRAggregate>(tile.Initializer);
        Assert.Equal(4, aggregate.Elements.Count);
        Assert.Equal((byte)3, Assert.IsType<IRConstant>(aggregate.Elements[2]).Value);
    }

    [Fact]
    public void TheShorthandArrayInitializerIsUnderstood()
    {
        // '= { ... }' is how tile data actually gets written. Roslyn models it as
        // a bare initializer with no array creation around it, which is a
        // different operation shape from 'new byte[] { ... }'.
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Art
            {
                public static readonly byte[] Shorthand = { 9, 8 };
                public static readonly byte[] Explicit = new byte[] { 9, 8 };
            }
            """));

        Assert.All(module.Globals, g => Assert.Equal(2, Assert.IsType<IRArrayType>(g.Type).Length));
        Assert.All(module.Globals, g => Assert.IsType<IRAggregate>(g.Initializer));
    }

    [Fact]
    public void MutableStaticsStayInWram()
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class State
            {
                public static byte[] Scratch = new byte[4];
            }
            """));

        Assert.False(Assert.Single(module.Globals).IsReadOnly);
    }

    [Fact]
    public void ArrayParametersLowerToPointers()
    {
        // C has no array parameters, and an undeclared array type carries length
        // zero, which would emit 'uint8_t data[0]' and not compile.
        IRModule module = TestHarness.CompileModule(TestHarness.Program(
            "        Display.Enable();",
            """
            public static class Helper
            {
                public static byte First(byte[] data) => data[0];
            }
            """));

        IRFunction first = module.Functions.Single(f => f.Name == "Helper_First");
        IRPointerType pointer = Assert.IsType<IRPointerType>(Assert.Single(first.Parameters).Type);

        Assert.Equal(IRPrimitiveType.U8, pointer.PointeeType);
    }
}
