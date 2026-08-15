namespace GBSharp.Compiler.IR;

/// <summary>The scalar kinds GB# understands.</summary>
public enum IRPrimitiveKind
{
    Void,
    Bool,
    U8,
    I8,
    U16,
    I16,

    /// <summary>Present so 32-bit code can be diagnosed rather than silently rejected.</summary>
    U32,

    /// <summary>Present so 32-bit code can be diagnosed rather than silently rejected.</summary>
    I32,
}

/// <summary>
/// A type in the GB# intermediate representation.
/// </summary>
/// <remarks>
/// Every IR type knows its size in bytes, because on this target the size is
/// the point. Resource reporting (thesis section 16) is a walk over these.
/// </remarks>
public abstract record IRType
{
    /// <summary>Storage size on the target.</summary>
    public abstract int SizeInBytes { get; }

    /// <summary>The name used in diagnostics and IR dumps.</summary>
    public abstract string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public sealed record IRPrimitiveType(IRPrimitiveKind Kind) : IRType
{
    public static readonly IRPrimitiveType Void = new(IRPrimitiveKind.Void);
    public static readonly IRPrimitiveType Bool = new(IRPrimitiveKind.Bool);
    public static readonly IRPrimitiveType U8 = new(IRPrimitiveKind.U8);
    public static readonly IRPrimitiveType I8 = new(IRPrimitiveKind.I8);
    public static readonly IRPrimitiveType U16 = new(IRPrimitiveKind.U16);
    public static readonly IRPrimitiveType I16 = new(IRPrimitiveKind.I16);
    public static readonly IRPrimitiveType U32 = new(IRPrimitiveKind.U32);
    public static readonly IRPrimitiveType I32 = new(IRPrimitiveKind.I32);

    public override int SizeInBytes => Kind switch
    {
        IRPrimitiveKind.Void => 0,
        IRPrimitiveKind.Bool or IRPrimitiveKind.U8 or IRPrimitiveKind.I8 => 1,
        IRPrimitiveKind.U16 or IRPrimitiveKind.I16 => 2,
        _ => 4,
    };

    /// <summary>Width in bits, used by the narrowing rules in lowering. Bool counts as 8.</summary>
    public int WidthInBits => SizeInBytes * 8;

    public bool IsInteger => Kind is not (IRPrimitiveKind.Void or IRPrimitiveKind.Bool);

    public bool IsSigned => Kind is IRPrimitiveKind.I8 or IRPrimitiveKind.I16 or IRPrimitiveKind.I32;

    public override string DisplayName => Kind switch
    {
        IRPrimitiveKind.Void => "void",
        IRPrimitiveKind.Bool => "bool",
        IRPrimitiveKind.U8 => "u8",
        IRPrimitiveKind.I8 => "i8",
        IRPrimitiveKind.U16 => "u16",
        IRPrimitiveKind.I16 => "i16",
        IRPrimitiveKind.U32 => "u32",
        _ => "i32",
    };

    /// <summary>The unsigned integer type that holds <paramref name="bits"/> bits.</summary>
    public static IRPrimitiveType Unsigned(int bits) => bits switch
    {
        <= 8 => U8,
        <= 16 => U16,
        _ => U32,
    };

    /// <summary>The signed integer type that holds <paramref name="bits"/> bits.</summary>
    public static IRPrimitiveType Signed(int bits) => bits switch
    {
        <= 8 => I8,
        <= 16 => I16,
        _ => I32,
    };
}

/// <summary>
/// A user-declared struct. Size is computed during lowering, when the field
/// layout is known, so that every later pass can ask a type what it costs.
/// </summary>
public sealed record IRStructType(string Name, int Size) : IRType
{
    public override int SizeInBytes => Size;

    public override string DisplayName => Name;
}

/// <summary>A fixed-length array. GB# has no other kind.</summary>
public sealed record IRArrayType(IRType ElementType, int Length) : IRType
{
    public override int SizeInBytes => ElementType.SizeInBytes * Length;

    public override string DisplayName => $"{ElementType.DisplayName}[{Length}]";
}

/// <summary>
/// A pointer, produced by <c>ref</c> parameters and by taking the address of an
/// array element. Always 2 bytes: the SM83 address space is 16-bit.
/// </summary>
public sealed record IRPointerType(IRType PointeeType) : IRType
{
    public override int SizeInBytes => 2;

    public override string DisplayName => $"{PointeeType.DisplayName}*";
}
