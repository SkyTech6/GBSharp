using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

/// <summary>One loop's estimated cost.</summary>
/// <param name="Span">The loop's own source position.</param>
/// <param name="PerIterationCycles">Estimated cost of one iteration.</param>
/// <param name="TripCount">The upper bound on iterations, or null if unknown.</param>
/// <param name="Bound">Where <paramref name="TripCount"/> came from.</param>
/// <param name="IsPartial">
/// True if the body reaches something whose cost depends on a runtime length.
/// </param>
/// <param name="IsFrameLoop">
/// True if this is a <c>while (true)</c> that waits for VBlank: the game's
/// frame loop, whose per-iteration cost is measured against a frame rather than
/// multiplied by anything.
/// </param>
public sealed record LoopCost(
    SourceSpan Span,
    int PerIterationCycles,
    int? TripCount,
    BoundSource Bound,
    bool IsPartial,
    bool IsFrameLoop)
{
    /// <summary>
    /// The whole loop's estimated cost, or null when the trip count is unknown.
    /// </summary>
    /// <remarks>
    /// Null rather than a sentinel, so there is no code path that can print a
    /// total for an unbounded loop. A <c>while (true)</c> runs forever and any
    /// number offered for it would be a fiction.
    /// </remarks>
    public int? TotalCycles => TripCount is { } trips ? PerIterationCycles * trips : null;
}

/// <summary>One function's estimated cost.</summary>
/// <param name="Name">The mangled name, matching <see cref="IRFunction.Name"/>.</param>
/// <param name="DisplayName">The C# name, for anything a developer reads.</param>
/// <param name="Cycles">
/// The function's own body, counting each call as the call itself and not as
/// what it reaches.
/// </param>
/// <param name="IsPartial">True if some cost in the body could not be known.</param>
/// <param name="IsCompilerGenerated">
/// True for a synthesised function, such as a specialised collection's
/// operations. Its cost is real and is charged wherever it is called from, but a
/// ranking meant for a human should leave it out: listing code the developer
/// did not write above the code they did does not answer "where has my time
/// gone".
/// </param>
/// <param name="Loops">Every loop in the body, outermost first.</param>
public sealed record FunctionCost(
    string Name,
    string DisplayName,
    int Cycles,
    bool IsPartial,
    bool IsCompilerGenerated,
    IReadOnlyList<LoopCost> Loops)
{
    /// <summary>The frame loop in this function, if it has one.</summary>
    public LoopCost? FrameLoop => Loops.FirstOrDefault(l => l.IsFrameLoop);
}

/// <summary>
/// Estimates what a function costs to run, from the IR alone.
/// </summary>
/// <remarks>
/// <para>
/// A third hand-written recursive walk over the IR, alongside <c>IRPrinter</c>
/// and the backend's <c>CEmitter</c>. That is deliberate rather than an
/// oversight. A shared visitor base introduced for the third consumer only pays
/// for itself if the first two are moved onto it, which would rewrite the two
/// files that every lowering and emission test asserts against; the three do not
/// share a return shape, two writing into a buffer and this one returning an
/// integer; and this walk is not uniform anyway, because it multiplies a loop
/// body by a trip count and takes the worse of a conditional's two arms, both of
/// which a general walker would have to be told about. If a fourth consumer
/// arrives, extract the base then, from three known shapes rather than two.
/// </para>
/// <para>
/// See <see cref="Sm83CostTable"/> for what the numbers mean and, more
/// importantly, for what they cannot.
/// </para>
/// </remarks>
public sealed class CostModel(IReadOnlyDictionary<string, int> capacities)
{
    /// <summary>The symbol whose presence makes a <c>while (true)</c> a frame loop.</summary>
    /// <remarks>
    /// <c>gbs_wait_vblank</c> is GBDK's <c>vsync()</c>, which genuinely blocks
    /// until the frame ends. A loop that waits on it runs once per frame by
    /// construction, which is what lets its per-iteration cost be compared
    /// against a frame instead of multiplied by an unknown.
    /// </remarks>
    public const string FrameBarrier = "gbs_wait_vblank";

    /// <summary>
    /// Loops with the order they were entered in, so the report can present them
    /// outermost first.
    /// </summary>
    /// <remarks>
    /// The walk finishes an inner loop before its enclosing one, so recording at
    /// the point a cost is known would list the nesting inside out. The entry
    /// order is taken on the way down and sorted on at the end.
    /// </remarks>
    private readonly List<(int Order, LoopCost Cost)> _loops = [];

    private int _entered;
    private bool _partial;

    /// <summary>Estimates one function, with no knowledge of its callees' bodies.</summary>
    public static FunctionCost Measure(IRFunction function, IReadOnlyDictionary<string, int> capacities)
    {
        var model = new CostModel(capacities);
        int cycles = model.Statement(function.Body);

        return new FunctionCost(
            function.Name,
            function.SourceName ?? function.Name,
            cycles,
            model._partial,
            function.IsCompilerGenerated,
            [.. model._loops.OrderBy(l => l.Order).Select(l => l.Cost)]);
    }

    private int Statement(IRStatement statement)
    {
        switch (statement)
        {
            case IRBlock block:
                return block.Statements.Sum(Statement);

            case IRLocalDeclaration declaration:
                return declaration.Initializer is { } initializer
                    ? Expression(initializer) + Store(declaration.Local.Type)
                    : 0;

            case IRAssign assign:
                return Expression(assign.Value) + Expression(assign.Target) + Store(assign.Target.Type);

            case IRCompoundAssign compound:
                return Expression(compound.Target)
                    + Expression(compound.Value)
                    + Sm83CostTable.BinaryCost(compound.Operator, compound.Target.Type, ConstantShift(compound.Value))
                    + Store(compound.Target.Type);

            case IRExpressionStatement expression:
                return Expression(expression.Expression);

            case IRIf conditional:
                // The worse arm, never the average: this is a worst-case model.
                return Expression(conditional.Condition)
                    + Sm83CostTable.Branch
                    + Math.Max(
                        Statement(conditional.Then),
                        conditional.Else is { } otherwise ? Statement(otherwise) : 0);

            case IRWhile loop:
            {
                int order = _entered++;
                bool outer = Enter();
                int body = Expression(loop.Condition) + Sm83CostTable.Branch + Statement(loop.Body);

                return Loop(
                    order,
                    loop.Span,
                    body,
                    trips: null,
                    BoundSource.None,
                    Leave(outer),
                    IsFrameLoop(loop));
            }

            case IRDoWhile loop:
            {
                int order = _entered++;
                bool outer = Enter();
                int body = Statement(loop.Body) + Expression(loop.Condition) + Sm83CostTable.Branch;

                return Loop(
                    order,
                    loop.Span,
                    body,
                    trips: null,
                    BoundSource.None,
                    Leave(outer),
                    isFrameLoop: false);
            }

            case IRFor loop:
                return For(loop);

            case IRSwitch selection:
                return Switch(selection);

            case IRBreak:
            case IRContinue:
                return Sm83CostTable.Branch;

            case IRReturn returned:
                return returned.Value is { } value ? Expression(value) : 0;

            default:
                // A statement the model has not been taught costs nothing rather
                // than throwing. An estimate missing a term is recoverable; an
                // internal compiler error during a report is not.
                return 0;
        }
    }

    private int For(IRFor loop)
    {
        int setup = loop.Initializers.Sum(Statement);
        int order = _entered++;
        bool outer = Enter();

        int body = Statement(loop.Body)
            + loop.Updates.Sum(Statement)
            + (loop.Condition is { } condition ? Expression(condition) : 0)
            + Sm83CostTable.Branch;

        bool bounded = LoopBounds.TryBound(loop, capacities, out int trips, out BoundSource source);

        return setup + Loop(order, loop.Span, body, bounded ? trips : null, source, Leave(outer), isFrameLoop: false);
    }

    /// <summary>
    /// Starts measuring a loop body's own partiality, returning the enclosing
    /// state to hand back to <see cref="Leave"/>.
    /// </summary>
    /// <remarks>
    /// Without this a loop would inherit partiality from any earlier statement in
    /// the same function, and "excluding a copy whose length is unknown" would be
    /// said about loops that contain no such copy.
    /// </remarks>
    private bool Enter()
    {
        bool outer = _partial;
        _partial = false;
        return outer;
    }

    /// <summary>Whether the body just walked was partial, restoring the enclosing state.</summary>
    private bool Leave(bool outer)
    {
        bool body = _partial;
        _partial = outer || body;
        return body;
    }

    /// <summary>
    /// Records a loop and returns what it contributes to the enclosing function.
    /// </summary>
    /// <remarks>
    /// An unbounded loop contributes one iteration to its caller's total. That is
    /// an understatement, and a deliberate one: the alternative is to contribute
    /// infinity, which would make every enclosing figure meaningless. The loop
    /// itself is recorded separately and reported on its own terms.
    /// </remarks>
    private int Loop(
        int order,
        SourceSpan span,
        int perIteration,
        int? trips,
        BoundSource source,
        bool partial,
        bool isFrameLoop)
    {
        _loops.Add((order, new LoopCost(span, perIteration, trips, source, partial, isFrameLoop)));

        return trips is { } count ? perIteration * count : perIteration;
    }

    /// <summary>
    /// A switch, charged as an if-chain over its sections.
    /// </summary>
    /// <remarks>
    /// The backend always emits a C <c>switch</c> and SDCC chooses between a jump
    /// table and a compare chain by how dense the case values are. GB# cannot see
    /// which it picked, so this charges the compare chain: the more expensive of
    /// the two, and therefore the one a worst-case model should assume.
    /// </remarks>
    private int Switch(IRSwitch selection)
    {
        int worst = selection.Default is { } fallback ? Statement(fallback) : 0;

        foreach (IRSwitchSection section in selection.Sections)
        {
            worst = Math.Max(worst, Statement(section.Body));
        }

        return Expression(selection.Value)
            + (selection.Sections.Count * (Sm83CostTable.ByteRegisterOp + Sm83CostTable.Branch))
            + worst;
    }

    private int Expression(IRExpression expression)
    {
        switch (expression)
        {
            // Folds into the consuming instruction's immediate, which that
            // instruction has already been charged for.
            case IRConstant:
            case IRUnit:
            case IRDataBlob:
                return 0;

            case IRAggregate aggregate:
                return aggregate.Elements.Sum(Expression);

            case IRDefaultValue value:
                return value.Type.SizeInBytes * Sm83CostTable.ByteMemoryOp;

            case IRLocalRef local:
                return Load(local.Local.Type);

            case IRParameterRef parameter:
                return Load(parameter.Parameter.Type);

            case IRGlobalRef global:
                return Math.Max(1, global.Global.Type.SizeInBytes) * Sm83CostTable.GlobalByte;

            case IRFieldAccess access:
                return Expression(access.Target) + Sm83CostTable.FieldOffsetAdd + Load(access.Type);

            case IRElementAccess access:
                return Expression(access.Target)
                    + Expression(access.Index)
                    + Sm83CostTable.IndexBase
                    + Scale(access.Type)
                    + Load(access.Type);

            case IRBinary binary:
                return Expression(binary.Left)
                    + Expression(binary.Right)
                    + Sm83CostTable.BinaryCost(binary.Operator, OperandType(binary), ConstantShift(binary.Right));

            case IRUnary unary:
                return Expression(unary.Operand)
                    + (Math.Max(1, unary.Type.SizeInBytes) * Sm83CostTable.ByteRegisterOp);

            case IRIncrement increment:
                return Expression(increment.Target)
                    + (Math.Max(1, increment.Type.SizeInBytes) * Sm83CostTable.ByteRegisterOp)
                    + Store(increment.Type);

            case IRCall call:
                // The callee's own body is not charged here. A call site pays for
                // the call; what it reaches is that function's own estimate.
                return call.Arguments.Sum(Expression) + Sm83CostTable.LocalCall;

            case IRNativeCall call:
                if (Sm83CostTable.KindOf(call.Symbol) == Sm83CostTable.NativeKind.BulkCopy)
                {
                    _partial = true;
                }

                return call.Arguments.Sum(Expression) + Sm83CostTable.NativeCallCost(call.Symbol);

            case IRConvert convert:
                return Expression(convert.Operand)
                    + Sm83CostTable.ConvertCost(convert.Operand.Type, convert.Type);

            case IRConditional conditional:
                return Expression(conditional.Condition)
                    + Sm83CostTable.Branch
                    + Math.Max(Expression(conditional.WhenTrue), Expression(conditional.WhenFalse));

            case IRAddressOf address:
                // Forming an address, not reading through it.
                return address.Operand is IRGlobalRef ? 0 : Sm83CostTable.FieldOffsetAdd;

            case IRDereference dereference:
                return Expression(dereference.Operand) + Sm83CostTable.PointerDeref;

            default:
                return 0;
        }
    }

    /// <summary>
    /// The width a binary operation runs at.
    /// </summary>
    /// <remarks>
    /// A comparison's result type is <c>bool</c>, but comparing two 16-bit
    /// values is 16-bit work. Taking the width from the operands rather than the
    /// result is what stops every comparison being costed as if it were 8-bit.
    /// </remarks>
    private static IRType OperandType(IRBinary binary) =>
        binary.Operator.IsComparison() ? binary.Left.Type : binary.Type;

    private static int Load(IRType type) => Math.Max(1, type.SizeInBytes) * Sm83CostTable.LocalByte;

    private static int Store(IRType type) => Math.Max(1, type.SizeInBytes) * Sm83CostTable.LocalByte;

    /// <summary>
    /// Scaling an index by the element width.
    /// </summary>
    /// <remarks>
    /// Free for single-byte elements, a shift chain for power-of-two widths, and
    /// a multiply helper otherwise. This is where an array of an odd-sized struct
    /// becomes visibly more expensive than an array of bytes, which is a real and
    /// non-obvious cost on this machine.
    /// </remarks>
    private static int Scale(IRType elementType)
    {
        int width = Math.Max(1, elementType.SizeInBytes);

        if (width == 1)
        {
            return 0;
        }

        if ((width & (width - 1)) == 0)
        {
            int shifts = 0;

            for (int remaining = width; remaining > 1; remaining >>= 1)
            {
                shifts++;
            }

            return shifts * 2 * Sm83CostTable.ShiftStep;
        }

        return Sm83CostTable.MultiplyHelper8;
    }

    /// <summary>A shift distance the model can see, so it need not assume one.</summary>
    private static int? ConstantShift(IRExpression value) =>
        LoopBounds.TryConstant(value, out long amount) && amount is >= 0 and < 64
            ? (int)amount
            : null;

    /// <summary>
    /// True if this is the frame loop: <c>while (true)</c> around a VBlank wait.
    /// </summary>
    private static bool IsFrameLoop(IRWhile loop) =>
        loop.Condition is IRConstant { Value: true }
        && IRWalk.Expressions(loop.Body)
            .Any(e => e is IRNativeCall { Symbol: FrameBarrier });
}
