namespace GBSharp.Tests.Compiler;

/// <summary>
/// The arithmetic width rules.
/// </summary>
/// <remarks>
/// The most consequential behaviour in the compiler. C# types
/// <c>byte + byte</c> as <c>int</c>; lowering that literally would make plainly
/// 8-bit code run 32-bit on an 8-bit CPU. These assert both halves of the
/// contract: narrow when narrowing is provably equivalent, and never narrow
/// silently when it is not.
/// </remarks>
public sealed class IntegerWidthTests
{
    [Fact]
    public void TruncatedAdditionStaysEightBit()
    {
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte a = 200;
                    byte b = 100;
                    byte sum = (byte)(a + b);
                    Sprites.Hide(sum);
            """));

        Assert.DoesNotContain("int16_t", c);
        Assert.DoesNotContain("int32_t", c);
    }

    [Fact]
    public void IncrementStaysEightBit()
    {
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte x = 80;
                    x++;
                    Sprites.Hide(x);
            """));

        Assert.Contains("x++;", c);
        Assert.DoesNotContain("int32_t", c);
    }

    [Fact]
    public void CompoundAssignmentStaysEightBit()
    {
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte x = 10;
                    x += 5;
                    Sprites.Hide(x);
            """));

        Assert.Contains("x += 5U;", c);
        Assert.DoesNotContain("int32_t", c);
    }

    [Fact]
    public void ComparisonWithAConstantDoesNotWiden()
    {
        // 'x < 80' must not widen because the literal 80 happens to be an int.
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte x = 10;
                    if (x < 80)
                    {
                        Sprites.Hide(x);
                    }
            """));

        Assert.Contains("(x < 80U)", c);
        Assert.DoesNotContain("int32_t", c);
    }

    [Fact]
    public void NestedShiftAndMaskStayEightBit()
    {
        // Each sub-expression is typed 'int' by C#. Narrowing has to see through
        // the nesting, or the outer operation widens everything back again.
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte frame = 40;
                    byte y = (byte)(64 + ((frame >> 3) & 7));
                    Sprites.Hide(y);
            """));

        Assert.Contains("(64U + ((frame >> 3U) & 7U))", c);
        Assert.DoesNotContain("int32_t", c);
    }

    [Fact]
    public void ArrayIndexDoesNotRoundTripThroughInt()
    {
        string c = TestHarness.EmitC(TestHarness.Program(
            """
                    byte i = 2;
                    Sprites.Hide(State.Lanes[i]);
            """,
            """
            public static class State
            {
                public static byte[] Lanes = new byte[4];
            }
            """));

        Assert.Contains("State_Lanes[i]", c);
        Assert.DoesNotContain("(int32_t)i", c);
    }

    [Fact]
    public void UntruncatedIntArithmeticIsReportedNotSilentlyNarrowed()
    {
        // 'a + b' really is 32-bit here: 200 + 100 does not fit in a byte, and
        // the developer asked for an int. GB# must say so rather than narrow.
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program("""
                    byte a = 200;
                    byte b = 100;
                    int wide = a + b;
                    Sprites.Hide((byte)wide);
            """));

        TestHarness.AssertReported(diagnostics, "GBS0007");
    }

    [Fact]
    public void SixteenBitMultiplicationIsReportedAsExpensive()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program("""
                    ushort a = 300;
                    ushort b = 5;
                    ushort product = (ushort)(a * b);
                    Sprites.Hide((byte)product);
            """));

        TestHarness.AssertReported(diagnostics, "GBS0102");
    }

    [Fact]
    public void EightBitMultiplicationIsNotFlagged()
    {
        // SDCC emits a compact helper at 8 bits. Warning here would be noise,
        // and a diagnostic that cries wolf gets suppressed and stops working.
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program("""
                    byte a = 3;
                    byte b = 5;
                    byte product = (byte)(a * b);
                    Sprites.Hide(product);
            """));

        TestHarness.AssertNotReported(diagnostics, "GBS0102");
    }

    [Fact]
    public void SixteenBitDivisionIsReportedAsExpensive()
    {
        var diagnostics = TestHarness.DiagnosticsFor(TestHarness.Program("""
                    ushort a = 1000;
                    ushort b = 7;
                    ushort q = (ushort)(a / b);
                    Sprites.Hide((byte)q);
            """));

        TestHarness.AssertReported(diagnostics, "GBS0103");
    }

    [Fact]
    public void UnsignedDivisionOfBytesNarrowsWithoutTruncation()
    {
        // a / b on two bytes always fits in a byte, so no cast is needed to
        // justify computing it at 8 bits.
        string c = TestHarness.EmitC(TestHarness.Program("""
                    byte a = 100;
                    byte b = 7;
                    byte q = (byte)(a / b);
                    Sprites.Hide(q);
            """));

        Assert.DoesNotContain("int32_t", c);
    }
}
