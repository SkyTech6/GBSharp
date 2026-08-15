using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Compiler.IR;

// The GB# intermediate representation.
//
// This IR is deliberately *structured*: loops and conditionals survive as
// loops and conditionals all the way to the backend, rather than being
// flattened into basic blocks and branches. Two requirements force that shape:
//
//   1. The generated C must be readable (thesis sections 3.3 and 9). A flattened
//      CFG lowers to goto, which no developer wants to read and which cannot be
//      mapped back to the C# it came from.
//   2. Cost and resource analysis (sections 7 and 16) want the loop structure,
//      because "this loop costs N cycles" is a statement about a loop.
//
// The cost is that classical dataflow optimisation is harder here. That trade is
// intentional: GB# delegates optimisation to SDCC and spends its own effort on
// making costs visible.

/// <summary>One compiled GB# program.</summary>
public sealed record IRModule(
    string Name,
    IReadOnlyList<IRStruct> Structs,
    IReadOnlyList<IRGlobal> Globals,
    IReadOnlyList<IRFunction> Functions,
    IRFunction EntryPoint)
{
    /// <summary>Converted assets, for the build report. Empty when none were declared.</summary>
    public IReadOnlyList<IRAsset> Assets { get; init; } = [];

    /// <summary>
    /// The resource budgets the source declared, checked once the ROM exists.
    /// </summary>
    public Budgets Budgets { get; init; } = Budgets.None;

    /// <summary>
    /// Estimated cycle costs and call depth, for the build report.
    /// </summary>
    /// <remarks>
    /// Computed during lowering, because the analysis is a walk over this IR and
    /// needs no toolchain, which is what lets <c>gbsharp analyze</c> report the
    /// same costs a build does. <see cref="Analysis.ModuleCostReport.Empty"/>
    /// when analysis did not run, so nothing needs a null check.
    /// </remarks>
    public Analysis.ModuleCostReport Costs { get; init; } = Analysis.ModuleCostReport.Empty;
}

/// <summary>
/// The resource budgets an assembly declares, if any.
/// </summary>
/// <param name="MaxWram">Bytes of work RAM, or null for no budget.</param>
/// <param name="MaxRom">Bytes of ROM image, or null.</param>
/// <param name="MaxRomBanks">16 KB banks including bank 0, or null.</param>
/// <remarks>
/// Carried on the module rather than checked in the frontend because none of it
/// can be answered until the linker has run: the truth about work RAM includes
/// the stack and the library's own state, and the ROM size is whatever the
/// cartridge ended up being.
/// </remarks>
public sealed record Budgets(int? MaxWram, int? MaxRom, int? MaxRomBanks)
{
    public static Budgets None { get; } = new(null, null, null);

    public bool Any => MaxWram is not null || MaxRom is not null || MaxRomBanks is not null;
}

/// <summary>How a bank was chosen.</summary>
public enum IRBankKind
{
    /// <summary>Bank 0, always mapped. The default.</summary>
    Resident,

    /// <summary>A bank the developer named.</summary>
    Fixed,

    /// <summary>A bank the build chooses and then reports.</summary>
    Automatic,
}

/// <summary>
/// Where a function's code or a global's bytes live in the cartridge.
/// </summary>
/// <remarks>
/// <para>
/// A value type whose <c>default</c> is <see cref="Resident"/>, so a node that
/// says nothing about banking is resident without anyone writing that down, and
/// nothing in the compiler needs a null check to ask where something lives.
/// </para>
/// <para>
/// This sits on declarations rather than on calls. A call's generated C is the
/// same whether or not the callee is banked: the backend synthesises the
/// trampoline from the callee's own declaration. So putting a bank on the call
/// site would be a second copy of the answer that could disagree with the first.
/// </para>
/// </remarks>
public readonly record struct IRBank(IRBankKind Kind, int Number)
{
    /// <summary>Bank 0: always mapped, and where everything starts.</summary>
    public static IRBank Resident => default;

    /// <summary>Placed by the build, then reported so it can be pinned.</summary>
    public static IRBank Automatic => new(IRBankKind.Automatic, 0);

    /// <summary>Placed in the bank the developer named.</summary>
    public static IRBank Fixed(int number) => new(IRBankKind.Fixed, number);

    public bool IsResident => Kind == IRBankKind.Resident;

    /// <summary>A short form for the IR dump and for diagnostics.</summary>
    public override string ToString() => Kind switch
    {
        IRBankKind.Fixed => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IRBankKind.Automatic => "auto",
        _ => "0",
    };
}

/// <param name="Name">The C# field that declared it.</param>
/// <param name="SourceFile">The image it came from.</param>
/// <param name="GlobalNames">The ROM globals it produced.</param>
public sealed record IRAsset(
    string Name,
    string SourceFile,
    Assets.AssetStats Stats,
    int RomBytes,
    IReadOnlyList<string> GlobalNames)
{
    /// <summary>Where the data landed. Reported; the globals are the authority.</summary>
    public IRBank Bank { get; init; }
}

/// <summary>A user-declared struct, in declaration order.</summary>
public sealed record IRStruct(string Name, IReadOnlyList<IRField> Fields)
{
    public int SizeInBytes => Fields.Sum(f => f.Type.SizeInBytes);
}

public sealed record IRField(string Name, IRType Type);

/// <summary>
/// A static field. These become C globals and are the whole of GB#'s WRAM
/// budget, so each one carries the source span that declared it for reporting.
/// </summary>
public sealed record IRGlobal(string Name, IRType Type, IRExpression? Initializer, SourceSpan Span)
{
    /// <summary>
    /// True if this global is immutable and can live in ROM rather than WRAM.
    /// </summary>
    /// <remarks>
    /// Set from <c>static readonly</c>. The backend emits <c>const</c>, which is
    /// what puts the bytes in the cartridge instead of the 8 KB of work RAM. A
    /// tileset that landed in WRAM would consume a tenth of it and be reported
    /// as WRAM, so this flag is the difference between the resource report
    /// telling the truth and telling a useful-sounding lie.
    /// </remarks>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// The ROM bank holding these bytes. Resident unless <c>[Bank]</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// Only meaningful when <see cref="IsReadOnly"/> is true. Work RAM is always
    /// mapped and is not banked on this hardware, so a mutable global is rejected
    /// rather than given a bank.
    /// </remarks>
    public IRBank Bank { get; init; }
}

/// <summary>A local variable. Reference identity distinguishes shadowed names.</summary>
public sealed class IRLocal(string name, IRType type)
{
    public string Name { get; } = name;

    public IRType Type { get; } = type;

    public override string ToString() => $"{Type.DisplayName} {Name}";
}

public sealed class IRParameter(string name, IRType type)
{
    public string Name { get; } = name;

    public IRType Type { get; } = type;

    public override string ToString() => $"{Type.DisplayName} {Name}";
}

/// <summary>A lowered method.</summary>
public sealed record IRFunction(
    string Name,
    IRType ReturnType,
    IReadOnlyList<IRParameter> Parameters,
    IReadOnlyList<IRLocal> Locals,
    IRBlock Body,
    SourceSpan Span)
{
    /// <summary>
    /// The C# name this came from, used in diagnostics and in the comment the
    /// emitter writes above the function.
    /// </summary>
    public string? SourceName { get; init; }

    /// <summary>
    /// The ROM bank holding this code. Resident unless <c>[Bank]</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// The backend groups functions by this into translation units, because GBDK
    /// selects a bank per file rather than per function.
    /// </remarks>
    public IRBank Bank { get; init; }

    /// <summary>
    /// True for a function the compiler synthesised rather than one the developer
    /// wrote, such as a specialised collection's operations.
    /// </summary>
    /// <remarks>
    /// Used to keep generated code out of rankings meant to answer "where has my
    /// time gone". The cost is real and still counted wherever it is called from;
    /// what is unhelpful is listing a function the developer did not write, and
    /// cannot readily change, above the ones they did.
    /// </remarks>
    public bool IsCompilerGenerated { get; init; }
}

// ---------------------------------------------------------------------------
// Statements
// ---------------------------------------------------------------------------

public abstract record IRStatement
{
    /// <summary>
    /// Where in the developer's C# this statement came from.
    /// </summary>
    /// <remarks>
    /// <see cref="Diagnostics.SourceSpan.None"/> for a statement with no single
    /// meaningful source position, such as an <see cref="IRBlock"/>: a block is
    /// a container, not a line of code, so there is nothing for it to point at.
    /// </remarks>
    public SourceSpan Span { get; init; } = SourceSpan.None;
}

public sealed record IRBlock(IReadOnlyList<IRStatement> Statements) : IRStatement
{
    public static IRBlock Empty { get; } = new([]);
}

/// <summary>Declares a local, optionally with an initial value.</summary>
public sealed record IRLocalDeclaration(IRLocal Local, IRExpression? Initializer) : IRStatement;

public sealed record IRAssign(IRExpression Target, IRExpression Value) : IRStatement;

/// <summary>
/// Assignment through an operator, e.g. <c>x += 1</c>. Kept distinct from
/// <see cref="IRAssign"/> so the backend can emit the compound form, which SDCC
/// generates better code for on an accumulator machine.
/// </summary>
public sealed record IRCompoundAssign(IRExpression Target, IRBinaryOperator Operator, IRExpression Value) : IRStatement;

public sealed record IRExpressionStatement(IRExpression Expression) : IRStatement;

public sealed record IRIf(IRExpression Condition, IRStatement Then, IRStatement? Else) : IRStatement;

public sealed record IRWhile(IRExpression Condition, IRStatement Body) : IRStatement;

public sealed record IRDoWhile(IRStatement Body, IRExpression Condition) : IRStatement;

public sealed record IRFor(
    IReadOnlyList<IRStatement> Initializers,
    IRExpression? Condition,
    IReadOnlyList<IRStatement> Updates,
    IRStatement Body) : IRStatement;

/// <summary>
/// A switch.
/// </summary>
/// <remarks>
/// The backend always emits a C <c>switch</c>; whether that becomes a jump table
/// or a chain of comparisons is SDCC's decision, taken from how dense the case
/// values are, and is not visible to GB#. The cost model therefore charges the
/// comparison chain, which is the more expensive of the two.
/// </remarks>
public sealed record IRSwitch(
    IRExpression Value,
    IReadOnlyList<IRSwitchSection> Sections,
    IRStatement? Default) : IRStatement;

public sealed record IRSwitchSection(IReadOnlyList<IRExpression> Values, IRStatement Body);

public sealed record IRBreak : IRStatement;

public sealed record IRContinue : IRStatement;

public sealed record IRReturn(IRExpression? Value) : IRStatement;

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

/// <summary>
/// An expression. Every node carries its own type: the backend never re-infers
/// one, so the width each operation runs at is decided exactly once, during
/// lowering.
/// </summary>
public abstract record IRExpression(IRType Type);

public sealed record IRConstant(IRType Type, object Value) : IRExpression(Type)
{
    public static IRConstant Bool(bool value) => new(IRPrimitiveType.Bool, value);

    public static IRConstant U8(byte value) => new(IRPrimitiveType.U8, value);
}

/// <summary>
/// Bulk immutable data produced by the asset pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IRAggregate"/>, which holds one node per element
/// and suits the handful of literals a developer writes by hand. A tileset is
/// tens of thousands of bytes and must not become tens of thousands of records.
/// </para>
/// <para>
/// Note that <see cref="ReadOnlyMemory{T}"/> has reference equality, so records
/// containing this node do not compare by value. Nothing should assert on
/// record equality of a blob.
/// </para>
/// </remarks>
public sealed record IRDataBlob(IRType Type, ReadOnlyMemory<byte> Bytes, int ElementWidth) : IRExpression(Type)
{
    public int ElementCount => Bytes.Length / ElementWidth;
}

/// <summary>
/// A braced list of element values, from an array initializer.
/// </summary>
/// <remarks>
/// Only ever appears as an <see cref="IRGlobal.Initializer"/>. C has no
/// expression form for this, so it is not a general expression despite the base
/// type: an aggregate reaching any other position is a lowering bug.
/// </remarks>
public sealed record IRAggregate(IRType Type, IReadOnlyList<IRExpression> Elements) : IRExpression(Type);

/// <summary>
/// The zero value of a type, from <c>default</c> or <c>new T()</c>.
/// </summary>
/// <remarks>
/// A separate node because zero means different things in C depending on the
/// type: a scalar takes a literal, but a struct cannot be assigned from one.
/// The backend emits a shared read-only zero instance per struct and copies
/// from it, which is one predictable struct copy rather than hidden per-field
/// stores.
/// </remarks>
public sealed record IRDefaultValue(IRType Type) : IRExpression(Type);

/// <summary>
/// A value that exists only in the type system and contributes nothing to the
/// generated C.
/// </summary>
/// <remarks>
/// Produced by <c>[NativeIdentity]</c> members that carry no value through, such
/// as <c>Hardware.Sprites</c>. A unit is dropped wherever an argument list is
/// built, so <c>Sprites.Move(0, x, y)</c> emits <c>gbs_sprite_move(0, x, y)</c>
/// with no receiver argument at all.
/// </remarks>
public sealed record IRUnit : IRExpression
{
    public static IRUnit Instance { get; } = new();

    private IRUnit() : base(IRPrimitiveType.Void)
    {
    }
}

public sealed record IRLocalRef(IRLocal Local) : IRExpression(Local.Type);

public sealed record IRParameterRef(IRParameter Parameter) : IRExpression(Parameter.Type);

public sealed record IRGlobalRef(IRGlobal Global) : IRExpression(Global.Type);

public sealed record IRFieldAccess(IRExpression Target, string FieldName, IRType Type) : IRExpression(Type);

public sealed record IRElementAccess(IRExpression Target, IRExpression Index, IRType Type) : IRExpression(Type);

public sealed record IRBinary(
    IRBinaryOperator Operator,
    IRExpression Left,
    IRExpression Right,
    IRType Type) : IRExpression(Type);

public sealed record IRUnary(IRUnaryOperator Operator, IRExpression Operand, IRType Type) : IRExpression(Type);

/// <summary>An increment or decrement used as a statement, e.g. <c>x++</c>.</summary>
public sealed record IRIncrement(IRExpression Target, bool IsDecrement, IRType Type) : IRExpression(Type);

/// <summary>A call to another GB# function in this module.</summary>
public sealed record IRCall(
    string FunctionName,
    IReadOnlyList<IRExpression> Arguments,
    IRType Type) : IRExpression(Type);

/// <summary>
/// A call straight through to a C symbol, from <c>[Native]</c>.
/// </summary>
/// <remarks>
/// This is the only place GB# code reaches the platform, and it is what makes
/// the framework and the developer's own escape hatches the same mechanism
/// (thesis section 19).
/// </remarks>
public sealed record IRNativeCall(
    string Symbol,
    IReadOnlyList<IRExpression> Arguments,
    IRType Type) : IRExpression(Type);

/// <summary>
/// An explicit width or sign conversion.
/// </summary>
/// <remarks>
/// C# promotes narrow integer arithmetic to <c>int</c>. Lowering makes every
/// surviving conversion explicit here so that a widening the developer did not
/// ask for is visible in the IR, reportable as GBS0101, and never invisible in
/// the generated C.
/// </remarks>
public sealed record IRConvert(IRExpression Operand, IRType Type) : IRExpression(Type);

/// <summary>A conditional expression, <c>c ? a : b</c>.</summary>
public sealed record IRConditional(
    IRExpression Condition,
    IRExpression WhenTrue,
    IRExpression WhenFalse,
    IRType Type) : IRExpression(Type);

public sealed record IRAddressOf(IRExpression Operand) : IRExpression(new IRPointerType(Operand.Type));

/// <summary>A dereference of a <c>ref</c> parameter.</summary>
public sealed record IRDereference(IRExpression Operand, IRType Type) : IRExpression(Type);

public enum IRBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
}

public enum IRUnaryOperator
{
    Negate,
    LogicalNot,
    BitwiseNot,
}

public static class IROperators
{
    /// <summary>True if the operator yields <c>bool</c> regardless of operand width.</summary>
    public static bool IsComparison(this IRBinaryOperator op) => op is
        IRBinaryOperator.Equal or IRBinaryOperator.NotEqual or
        IRBinaryOperator.LessThan or IRBinaryOperator.LessThanOrEqual or
        IRBinaryOperator.GreaterThan or IRBinaryOperator.GreaterThanOrEqual;

    /// <summary>
    /// True if the operator is congruent modulo 2^n, so computing it at a narrow
    /// width and computing it wide then truncating give the same answer.
    /// </summary>
    /// <remarks>
    /// This is what makes it sound to evaluate <c>(byte)(a + b)</c> in 8 bits.
    /// Division, remainder and right shift are excluded: they read the high bits
    /// that truncation would have discarded.
    /// </remarks>
    public static bool IsCongruentModuloWidth(this IRBinaryOperator op) => op is
        IRBinaryOperator.Add or IRBinaryOperator.Subtract or IRBinaryOperator.Multiply or
        IRBinaryOperator.BitwiseAnd or IRBinaryOperator.BitwiseOr or IRBinaryOperator.BitwiseXor or
        IRBinaryOperator.ShiftLeft;

    /// <summary>
    /// True if the result of the operator on unsigned operands always fits in
    /// the wider operand's type, so narrowing needs no truncation to justify it.
    /// </summary>
    public static bool ResultFitsOperandWidth(this IRBinaryOperator op) => op is
        IRBinaryOperator.Divide or IRBinaryOperator.Remainder or
        IRBinaryOperator.ShiftRight or IRBinaryOperator.BitwiseAnd or
        IRBinaryOperator.BitwiseOr or IRBinaryOperator.BitwiseXor;
}
