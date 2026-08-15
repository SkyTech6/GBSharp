using System.Globalization;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// The width rules that decide what size each arithmetic operation runs at.
/// </summary>
/// <remarks>
/// <para>
/// C# promotes narrow integer arithmetic to <c>int</c>: <c>byte + byte</c> has
/// type <c>int</c>. Lowering that literally would emit 32-bit arithmetic in C
/// for code that plainly looks 8-bit, which on an 8-bit CPU is the single most
/// expensive mistake this compiler could make, and an invisible one.
/// </para>
/// <para>
/// So GB# computes the width an operation *needs* rather than the width C# says
/// it has, and narrows when narrowing is provably equivalent. Where it is not
/// provable, the width stays wide and GBS0007 or GBS0101 says so out loud. The
/// rule is never to be quietly slower than the source looks.
/// </para>
/// </remarks>
internal static class IntegerWidth
{
    /// <summary>The primitive type of an expression, or null if it is not a primitive.</summary>
    public static IRPrimitiveType? AsPrimitive(IRType? type) => type as IRPrimitiveType;

    /// <summary>
    /// The narrowest type that holds <paramref name="value"/>, preferring
    /// unsigned. Used so <c>x &lt; 80</c> compares at 8 bits rather than
    /// widening because the literal happens to be typed <c>int</c>.
    /// </summary>
    public static IRPrimitiveType? ForConstant(object? value)
    {
        if (value is bool)
        {
            return IRPrimitiveType.Bool;
        }

        if (value is null || !TryToInt64(value, out long v))
        {
            return null;
        }

        return v switch
        {
            >= 0 and <= byte.MaxValue => IRPrimitiveType.U8,
            >= sbyte.MinValue and < 0 => IRPrimitiveType.I8,
            >= 0 and <= ushort.MaxValue => IRPrimitiveType.U16,
            >= short.MinValue and < 0 => IRPrimitiveType.I16,
            >= 0 and <= uint.MaxValue => IRPrimitiveType.U32,
            _ => IRPrimitiveType.I32,
        };
    }

    /// <summary>True if <paramref name="value"/> is representable in <paramref name="type"/>.</summary>
    public static bool Fits(object? value, IRPrimitiveType type)
    {
        if (value is bool)
        {
            return type.Kind == IRPrimitiveKind.Bool;
        }

        if (value is null || !TryToInt64(value, out long v))
        {
            return false;
        }

        return type.Kind switch
        {
            IRPrimitiveKind.U8 => v is >= byte.MinValue and <= byte.MaxValue,
            IRPrimitiveKind.I8 => v is >= sbyte.MinValue and <= sbyte.MaxValue,
            IRPrimitiveKind.U16 => v is >= ushort.MinValue and <= ushort.MaxValue,
            IRPrimitiveKind.I16 => v is >= short.MinValue and <= short.MaxValue,
            IRPrimitiveKind.U32 => v is >= 0 and <= uint.MaxValue,
            IRPrimitiveKind.I32 => v is >= int.MinValue and <= int.MaxValue,
            _ => false,
        };
    }

    /// <summary>
    /// Stores a constant in the CLR type matching its IR type, so the emitter can
    /// print it directly without re-deciding the width.
    /// </summary>
    /// <remarks>
    /// Shared by <c>FunctionLowerer</c> for expression constants and by
    /// <c>ModuleLowerer</c> for the elements of a global's array initializer,
    /// so both reach the same C literal for the same source literal.
    /// </remarks>
    public static object Normalize(object value, IRType type)
    {
        if (type is not IRPrimitiveType primitive || value is bool)
        {
            return value;
        }

        try
        {
            return primitive.Kind switch
            {
                IRPrimitiveKind.U8 => Convert.ToByte(value, CultureInfo.InvariantCulture),
                IRPrimitiveKind.I8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                IRPrimitiveKind.U16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                IRPrimitiveKind.I16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                IRPrimitiveKind.U32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                IRPrimitiveKind.I32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                _ => value,
            };
        }
        catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException)
        {
            return value;
        }
    }

    /// <summary>
    /// The smallest type that can hold every value of both operands.
    /// </summary>
    /// <remarks>
    /// Mixing a signed and an unsigned type of the same width needs the next
    /// width up to represent both, which mirrors C#'s own promotion rule and
    /// keeps the generated C from relying on implementation-defined conversions.
    /// </remarks>
    public static IRPrimitiveType Wider(IRPrimitiveType left, IRPrimitiveType right)
    {
        if (left == right)
        {
            return left;
        }

        if (left.Kind == IRPrimitiveKind.Bool || right.Kind == IRPrimitiveKind.Bool)
        {
            return IRPrimitiveType.U8;
        }

        int width = Math.Max(left.WidthInBits, right.WidthInBits);
        bool signed = left.IsSigned || right.IsSigned;

        if (signed)
        {
            bool unsignedAtFullWidth =
                (!left.IsSigned && left.WidthInBits == width) ||
                (!right.IsSigned && right.WidthInBits == width);

            if (unsignedAtFullWidth)
            {
                width = Math.Min(width * 2, 32);
            }

            return IRPrimitiveType.Signed(width);
        }

        return IRPrimitiveType.Unsigned(width);
    }

    /// <summary>True if converting from <paramref name="from"/> to <paramref name="to"/> loses nothing.</summary>
    public static bool IsWidening(ITypeSymbol? from, ITypeSymbol? to)
    {
        if (from is null || to is null)
        {
            return false;
        }

        int? fromWidth = IntegralWidth(from.SpecialType);
        int? toWidth = IntegralWidth(to.SpecialType);

        return fromWidth is not null && toWidth is not null && toWidth >= fromWidth;
    }

    private static int? IntegralWidth(SpecialType type) => type switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => 8,
        SpecialType.System_UInt16 or SpecialType.System_Int16 => 16,
        SpecialType.System_UInt32 or SpecialType.System_Int32 => 32,
        _ => null,
    };

    private static bool TryToInt64(object value, out long result)
    {
        switch (value)
        {
            case byte b: result = b; return true;
            case sbyte b: result = b; return true;
            case short s: result = s; return true;
            case ushort s: result = s; return true;
            case int i: result = i; return true;
            case uint u: result = u; return true;
            case long l: result = l; return true;
            case ulong u when u <= long.MaxValue: result = (long)u; return true;
            case char c: result = c; return true;
            default: result = 0; return false;
        }
    }
}
