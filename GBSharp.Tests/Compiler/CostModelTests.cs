using System.Text.RegularExpressions;
using GBSharp.Compiler.Analysis;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// What the cost model claims about one operation relative to another.
/// </summary>
/// <remarks>
/// <para>
/// These assert orderings, not values. A test that pinned an estimate to 240
/// would fail on every future refinement of the table while passing forever if
/// the model were simply wrong, which is the opposite of what a test is for. The
/// claims the model actually makes to a developer are comparative: a multiply
/// costs more than a shift, a banked call costs more than a local one, 16-bit
/// work costs more than 8-bit, and those are what break when the table is
/// edited wrongly.
/// </para>
/// <para>
/// The exceptions are the figures that are contracts rather than estimates: the
/// length of a frame, and the cost of a call, both of which are exact.
/// </para>
/// </remarks>
public sealed class CostModelTests
{
    /// <summary>Estimates the cost of a method body.</summary>
    private static FunctionCost Measure(string body, string extra = "")
    {
        IRModule module = TestHarness.CompileModule(TestHarness.Program(body, extra));

        return CostModel.Measure(module.EntryPoint, LoopBounds.CollectionCapacities(module));
    }

    private static int Cycles(string body, string extra = "") => Measure(body, extra).Cycles;

    [Fact]
    public void AFrameIsSeventyThousandTwoHundredAndTwentyFourCycles()
    {
        // 154 scanlines of 456 cycles. Hardware, not an estimate: this is the
        // denominator every percentage in the band is quoted against.
        Assert.Equal(70_224, Sm83CostTable.FrameCycles);
        Assert.Equal(59.7, Math.Round(Sm83CostTable.FramesPerSecond, 1));
    }

    [Fact]
    public void ACallCostsACallAndAReturn()
    {
        Assert.Equal(24 + 16, Sm83CostTable.LocalCall);
    }

    [Fact]
    public void WiderArithmeticCostsMore()
    {
        int eight = Cycles("        byte a = 1, b = 2; byte c = (byte)(a + b); Sprites[0].X = c;");
        int sixteen = Cycles("        ushort a = 1, b = 2; ushort c = (ushort)(a + b); Sprites[0].X = (byte)c;");
        int thirtyTwo = Cycles("        int a = 1, b = 2; int c = a + b; Sprites[0].X = (byte)c;");

        Assert.True(eight < sixteen, $"8-bit {eight} should cost less than 16-bit {sixteen}");
        Assert.True(sixteen < thirtyTwo, $"16-bit {sixteen} should cost less than 32-bit {thirtyTwo}");
    }

    /// <summary>
    /// SM83 has no multiply instruction, so a multiply is a library call and a
    /// shift is an instruction. That difference is the whole content of GBS0102.
    /// </summary>
    [Fact]
    public void MultiplyingCostsMoreThanShifting()
    {
        int shift = Cycles("        ushort a = 3; ushort b = (ushort)(a << 1); Sprites[0].X = (byte)b;");
        int multiply = Cycles("        ushort a = 3; ushort b = (ushort)(a * 5); Sprites[0].X = (byte)b;");

        Assert.True(multiply > shift, $"multiply {multiply} should cost more than shift {shift}");
    }

    [Fact]
    public void DividingCostsMoreThanMultiplying()
    {
        int multiply = Cycles("        ushort a = 300; ushort b = (ushort)(a * 5); Sprites[0].X = (byte)b;");
        int divide = Cycles("        ushort a = 300; ushort b = (ushort)(a / 5); Sprites[0].X = (byte)b;");

        Assert.True(divide > multiply, $"divide {divide} should cost more than multiply {multiply}");
    }

    /// <summary>The same routine computes both, so which result is used changes nothing.</summary>
    [Fact]
    public void RemainderCostsWhatDivisionCosts()
    {
        int divide = Cycles("        ushort a = 300; ushort b = (ushort)(a / 5); Sprites[0].X = (byte)b;");
        int remainder = Cycles("        ushort a = 300; ushort b = (ushort)(a % 5); Sprites[0].X = (byte)b;");

        Assert.Equal(divide, remainder);
    }

    [Fact]
    public void ReachingFurtherToCallCostsMore()
    {
        Assert.True(Sm83CostTable.BankedCall > Sm83CostTable.RuntimeCall);
        Assert.True(Sm83CostTable.RuntimeCall > Sm83CostTable.LocalCall);
        Assert.True(Sm83CostTable.LocalCall > Sm83CostTable.InlineShimCall);
    }

    /// <summary>
    /// The figure GBS0301's help text quotes. Read from the table so the prose and
    /// the model cannot drift apart.
    /// </summary>
    [Fact]
    public void ABankedCallCostsATrampolineOverALocalOne()
    {
        Assert.Equal(Sm83CostTable.BankedCall - Sm83CostTable.LocalCall, Sm83CostTable.BankedCallOverhead);
        Assert.True(Sm83CostTable.BankedCallOverhead > 0);
    }

    /// <summary>
    /// An absolute load cannot fold into a shorter form or live in a register
    /// across a call, so touching a static field really is dearer than a local.
    /// </summary>
    /// <summary>
    /// Isolated as the difference one extra read makes, so the declaration and
    /// the store either side of it cancel out rather than being compared.
    /// </summary>
    [Fact]
    public void ReadingAGlobalCostsMoreThanReadingALocal()
    {
        const string data = """
            public static class Data
            {
                public static byte Counter;
            }
            """;

        int local = Cycles("        byte a = 1; Sprites[0].X = a; Sprites[1].X = a;")
            - Cycles("        byte a = 1; Sprites[0].X = a;");

        int global = Cycles("        Sprites[0].X = Data.Counter; Sprites[1].X = Data.Counter;", data)
            - Cycles("        Sprites[0].X = Data.Counter;", data);

        Assert.True(global > local, $"a global read {global} should cost more than a local read {local}");
        Assert.True(Sm83CostTable.GlobalByte > Sm83CostTable.LocalByte);
    }

    /// <summary>
    /// An index into an array of wide elements has to be scaled, and a struct
    /// whose size is not a power of two costs a multiply helper to scale.
    /// </summary>
    [Fact]
    public void IndexingWiderElementsCostsMore()
    {
        const string bytes = """
            public static class Data
            {
                public static FixedArray<byte> Values;
            }
            """;

        const string structs = """
            public struct Wide
            {
                public ushort A;
                public ushort B;
            }

            public static class Data
            {
                public static FixedArray<Wide> Values;
            }
            """;

        int narrow = Cycles("        Sprites[0].X = Data.Values[2];", Capacity(bytes));
        int wide = Cycles("        Sprites[0].X = (byte)Data.Values[2].A;", Capacity(structs));

        Assert.True(wide > narrow, $"wide element {wide} should cost more than byte element {narrow}");
    }

    /// <summary>
    /// The cast back to byte is free; the widening it undoes was not. That is
    /// exactly what GBS0101 is trying to teach.
    /// </summary>
    [Fact]
    public void NarrowingIsFreeAndWideningIsNot()
    {
        Assert.Equal(0, Sm83CostTable.ConvertCost(IRPrimitiveType.U16, IRPrimitiveType.U8));
        Assert.True(Sm83CostTable.ConvertCost(IRPrimitiveType.U8, IRPrimitiveType.U16) > 0);

        // Sign extension has to test and propagate the sign bit, so it costs more
        // than zeroing a high byte.
        Assert.True(
            Sm83CostTable.ConvertCost(IRPrimitiveType.I8, IRPrimitiveType.I16)
            > Sm83CostTable.ConvertCost(IRPrimitiveType.U8, IRPrimitiveType.U16));
    }

    [Fact]
    public void AddingWorkNeverLowersTheEstimate()
    {
        int one = Cycles("        byte a = 1; Sprites[0].X = a;");
        int two = Cycles("        byte a = 1; byte b = 2; Sprites[0].X = (byte)(a + b);");

        Assert.True(two > one, $"more work {two} should not cost less than less work {one}");
    }

    /// <summary>
    /// A worst-case model takes the dearer arm. Charging the sum would claim both
    /// arms run; charging the average would claim to know which one does.
    /// </summary>
    [Fact]
    public void AConditionalIsChargedItsWorseArmNotBoth()
    {
        int cheapOnly = Cycles("""
                    byte a = 1;
                    if (Input.Right) { a++; }
            """);

        int both = Cycles("""
                    byte a = 1;
                    if (Input.Right) { a++; } else { a = (byte)(a * 7 * 9); }
            """);

        int sumOfArms = Cycles("""
                    byte a = 1;
                    if (Input.Right) { a++; }
                    a = (byte)(a * 7 * 9);
            """);

        Assert.True(both > cheapOnly, "the expensive arm should raise the estimate");
        Assert.True(both < sumOfArms, "an if/else should cost less than running both arms in sequence");
    }

    /// <summary>
    /// gbs_runtime.h says in prose that "testing several buttons costs several
    /// reads". The model turns that sentence into a number.
    /// </summary>
    [Fact]
    public void EachInputTestSamplesTheJoypad()
    {
        int one = Cycles("        if (Input.Right) { Sprites[0].X = 1; }");
        int two = Cycles("        if (Input.Right) { Sprites[0].X = 1; } if (Input.Left) { Sprites[0].X = 2; }");

        Assert.True(two - one >= Sm83CostTable.JoypadRead, "a second button test should cost a second joypad read");
        Assert.Equal(Sm83CostTable.NativeKind.Joypad, Sm83CostTable.KindOf("gbs_input_left"));
    }

    /// <summary>
    /// The inline/banked split is a fact about the runtime, not a modelling
    /// convenience: the header holds what compiles away, the .c holds what has to
    /// switch banks. This is the test that keeps the table honest as the runtime
    /// grows a function.
    /// </summary>
    [Fact]
    public void EveryRuntimeCFunctionIsClassifiedAsMoreThanAnInlineShim()
    {
        string source = Path.Combine(
            TestHarness.RepositoryRoot(),
            "GBSharp.Backend.GBDK",
            "Runtime",
            "gbs_runtime.c");

        Assert.True(File.Exists(source), source + " should exist");

        var defined = Regex
            .Matches(File.ReadAllText(source), @"^[A-Za-z_][A-Za-z_0-9 ]*\**\s*(gbs_[a-z_0-9]+)\s*\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            // gbs_map is file-static plumbing, never named by a [Native] member.
            .Where(name => name != "gbs_map")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(defined);

        var misclassified = defined
            .Where(name => Sm83CostTable.KindOf(name) is Sm83CostTable.NativeKind.InlineShim)
            .ToList();

        Assert.True(
            misclassified.Count == 0,
            "defined in gbs_runtime.c but costed as an inlined header wrapper: " + string.Join(", ", misclassified));
    }

    [Fact]
    public void AnythingNotInTheRuntimeCFileIsAnInlineShim()
    {
        Assert.Equal(Sm83CostTable.NativeKind.InlineShim, Sm83CostTable.KindOf("set_bkg_tile_xy"));
        Assert.Equal(Sm83CostTable.NativeKind.BulkCopy, Sm83CostTable.KindOf("gbs_background_load"));
        Assert.Equal(Sm83CostTable.NativeKind.Runtime, Sm83CostTable.KindOf("gbs_bank_switch"));
    }

    /// <summary>
    /// A loader whose length is a runtime value cannot be costed, and the estimate
    /// says so rather than quietly omitting it.
    /// </summary>
    [Fact]
    public void ACopyOfUnknownLengthMakesTheEstimatePartial()
    {
        Assert.False(Measure("        Sprites[0].X = 1;").IsPartial);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 40)]
    [InlineData(999, 999)]
    [InlineData(2_141, 2_100)]
    [InlineData(41_337, 41_000)]
    public void EstimatesAreRoundedToThePrecisionTheModelSupports(int cycles, int expected) =>
        Assert.Equal(expected, Sm83CostTable.RoundForDisplay(cycles));

    [Fact]
    public void APercentageOfAFrameIsRelativeToTheFrame()
    {
        Assert.Equal(100, Sm83CostTable.PercentOfFrame(Sm83CostTable.FrameCycles));
        Assert.Equal(50, Sm83CostTable.PercentOfFrame(Sm83CostTable.FrameCycles / 2));
        Assert.Equal(0, Sm83CostTable.PercentOfFrame(0));
    }

    /// <summary>C# has no value type parameters, so capacity sits on the field.</summary>
    private static string Capacity(string source) => source.Replace(
        "public static FixedArray",
        "[Capacity(8)] public static FixedArray",
        StringComparison.Ordinal);
}
