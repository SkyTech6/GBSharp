using System.Globalization;
using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

/// <summary>Where a loop's trip count came from.</summary>
public enum BoundSource
{
    /// <summary>Not known. The loop is reported per iteration, or not at all.</summary>
    None,

    /// <summary>The loop's own constant bound.</summary>
    Constant,

    /// <summary>
    /// The capacity of the fixed collection being iterated, which the count
    /// cannot exceed.
    /// </summary>
    Capacity,
}

/// <summary>
/// Recovers a trip count from a <see cref="IRFor"/> where one is soundly available.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. It recognises the shape lowering produces for a counted
/// <c>for</c> and refuses everything else, because each relaxation of the shape
/// is another chance to state a bound confidently and be wrong. There is no
/// constant propagation and no value-range analysis here: the IR is structured
/// rather than a control-flow graph, and adding dataflow to bound a loop would
/// be a much larger change than the number is worth.
/// </para>
/// <para>
/// The capacity rule is what makes this worth having at all. Written the
/// obvious way, a loop over a fixed list reads <c>i &lt; enemies.Count</c>,
/// which lowers to a runtime field read and would be unanalysable, and that is
/// the shape the samples actually use. But <c>count</c> can never exceed the
/// collection's capacity: the generated <c>Add</c> refuses past it. So the
/// capacity is a sound upper bound, and a worst-case estimate is exactly what
/// wants one.
/// </para>
/// <para>
/// A <c>break</c> or <c>continue</c> in the body leaves the count an upper
/// bound rather than an exact one, which is also what a worst-case estimate
/// wants. The diagnostics say so rather than treating it as unknown.
/// </para>
/// </remarks>
public static class LoopBounds
{
    /// <summary>The field a fixed list keeps its length in.</summary>
    private const string CountField = "count";

    /// <summary>The field a fixed collection keeps its storage in.</summary>
    private const string ItemsField = "items";

    /// <summary>
    /// The capacity of every specialised fixed collection in a module, by struct name.
    /// </summary>
    /// <remarks>
    /// Read back out of the IR rather than passed down from the lowerer. The
    /// specialised struct holds its storage as an array whose length <em>is</em>
    /// the capacity, so the fact is already in the module and threading it
    /// through a second channel would be a copy that could disagree.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> CollectionCapacities(IRModule module)
    {
        var capacities = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (IRStruct declaration in module.Structs)
        {
            foreach (IRField field in declaration.Fields)
            {
                if (field.Name == ItemsField && field.Type is IRArrayType array)
                {
                    capacities[declaration.Name] = array.Length;
                }
            }
        }

        return capacities;
    }

    /// <summary>
    /// The number of times a loop can run, if that is soundly knowable.
    /// </summary>
    /// <param name="loop">The loop.</param>
    /// <param name="capacities">
    /// From <see cref="CollectionCapacities"/>. Empty is valid and simply means
    /// no loop will be bounded by a capacity.
    /// </param>
    /// <param name="trips">The upper bound on iterations.</param>
    /// <param name="source">Where the bound came from.</param>
    public static bool TryBound(
        IRFor loop,
        IReadOnlyDictionary<string, int> capacities,
        out int trips,
        out BoundSource source)
    {
        trips = 0;
        source = BoundSource.None;

        if (loop.Condition is null
            || loop.Initializers.Count != 1
            || loop.Updates.Count != 1
            || loop.Initializers[0] is not IRLocalDeclaration { Initializer: { } start } declaration
            || !TryConstant(start, out long from))
        {
            return false;
        }

        IRLocal counter = declaration.Local;

        if (!TryLimit(loop.Condition, counter, capacities, out long limit, out bool inclusive, out source)
            || !TryStep(loop.Updates[0], counter, out long step)
            || Writes(loop.Body, counter)
            || Underflows(counter, limit, inclusive, step))
        {
            source = BoundSource.None;
            return false;
        }

        long distance = step > 0
            ? limit - from + (inclusive ? 1 : 0)
            : from - limit + (inclusive ? 1 : 0);

        long magnitude = Math.Abs(step);

        if (distance <= 0)
        {
            // The loop never runs. Sound, and worth saying rather than refusing.
            trips = 0;
            return true;
        }

        long count = (distance + magnitude - 1) / magnitude;

        // A trip count past what a 16-bit counter could reach is not a loop GB#
        // understood; it is a sign the shape matched something it should not have.
        if (count > int.MaxValue)
        {
            source = BoundSource.None;
            return false;
        }

        trips = (int)count;
        return true;
    }

    /// <summary>
    /// True if the loop counts down through zero on an unsigned counter, and so
    /// never terminates at all.
    /// </summary>
    /// <remarks>
    /// <c>for (byte i = 10; i >= 0; i--)</c> looks like eleven iterations and is
    /// an infinite loop: the counter wraps to 255 rather than going below zero,
    /// and the condition is true for every value an unsigned byte can hold. The
    /// arithmetic below would happily report eleven, which is the most dangerous
    /// kind of wrong: a plausible number for a loop that never ends. Refusing to
    /// bound it means the estimate says nothing rather than something false.
    /// </remarks>
    private static bool Underflows(IRLocal counter, long limit, bool inclusive, long step) =>
        step < 0
        && inclusive
        && limit <= 0
        && counter.Type is IRPrimitiveType { IsInteger: true, IsSigned: false };

    /// <summary>
    /// The condition's upper bound, if the condition compares the counter against
    /// something knowable.
    /// </summary>
    private static bool TryLimit(
        IRExpression condition,
        IRLocal counter,
        IReadOnlyDictionary<string, int> capacities,
        out long limit,
        out bool inclusive,
        out BoundSource source)
    {
        limit = 0;
        inclusive = false;
        source = BoundSource.None;

        if (Strip(condition) is not IRBinary binary)
        {
            return false;
        }

        IRExpression left = Strip(binary.Left);
        IRExpression right = Strip(binary.Right);

        IRBinaryOperator op = binary.Operator;
        IRExpression bound;

        if (IsRef(left, counter))
        {
            bound = right;
        }
        else if (IsRef(right, counter))
        {
            // `4 > i` is `i < 4` with the operands the other way round.
            bound = left;
            op = Mirror(op);
        }
        else
        {
            return false;
        }

        switch (op)
        {
            case IRBinaryOperator.LessThan:
            case IRBinaryOperator.GreaterThan:
                inclusive = false;
                break;

            case IRBinaryOperator.LessThanOrEqual:
            case IRBinaryOperator.GreaterThanOrEqual:
                inclusive = true;
                break;

            default:
                return false;
        }

        if (TryConstant(bound, out limit))
        {
            source = BoundSource.Constant;
            return true;
        }

        if (TryCapacity(bound, capacities, out int capacity))
        {
            // `i < list.Count` runs at most Capacity times, because Count can
            // never exceed it. Exclusive either way: a count of N indexes 0..N-1.
            limit = capacity;
            inclusive = false;
            source = BoundSource.Capacity;
            return true;
        }

        return false;
    }

    /// <summary>The counter's constant step, from an increment or a compound assignment.</summary>
    private static bool TryStep(IRStatement update, IRLocal counter, out long step)
    {
        step = 0;

        switch (update)
        {
            case IRExpressionStatement { Expression: IRIncrement increment }
                when IsRef(Strip(increment.Target), counter):
                step = increment.IsDecrement ? -1 : 1;
                return true;

            case IRCompoundAssign compound when IsRef(Strip(compound.Target), counter):
                if (!TryConstant(compound.Value, out long amount))
                {
                    return false;
                }

                step = compound.Operator switch
                {
                    IRBinaryOperator.Add => amount,
                    IRBinaryOperator.Subtract => -amount,
                    _ => 0,
                };

                return step != 0;

            default:
                return false;
        }
    }

    /// <summary>
    /// True if the bound is the <c>count</c> of a specialised fixed collection.
    /// </summary>
    private static bool TryCapacity(
        IRExpression bound,
        IReadOnlyDictionary<string, int> capacities,
        out int capacity)
    {
        capacity = 0;

        return bound is IRFieldAccess { FieldName: CountField } access
            && access.Target.Type is IRStructType structType
            && capacities.TryGetValue(structType.Name, out capacity);
    }

    /// <summary>
    /// True if anything in the body could change the counter, which would make
    /// the trip count a fiction.
    /// </summary>
    /// <remarks>
    /// Includes taking its address: a counter passed by <c>ref</c> can be written
    /// through somewhere this walk cannot see.
    /// </remarks>
    private static bool Writes(IRStatement statement, IRLocal counter) =>
        IRWalk.Expressions(statement).Any(e => e switch
        {
            IRIncrement increment => IsRef(Strip(increment.Target), counter),
            IRAddressOf address => IsRef(Strip(address.Operand), counter),
            _ => false,
        })
        || IRWalk.Statements(statement).Any(s => s switch
        {
            IRAssign assign => IsRef(Strip(assign.Target), counter),
            IRCompoundAssign compound => IsRef(Strip(compound.Target), counter),
            _ => false,
        });

    /// <summary>
    /// Looks through conversions, which lowering inserts freely around a counter
    /// and which never change which variable is being read.
    /// </summary>
    private static IRExpression Strip(IRExpression expression) =>
        expression is IRConvert convert ? Strip(convert.Operand) : expression;

    private static bool IsRef(IRExpression expression, IRLocal counter) =>
        expression is IRLocalRef reference && ReferenceEquals(reference.Local, counter);

    private static IRBinaryOperator Mirror(IRBinaryOperator op) => op switch
    {
        IRBinaryOperator.LessThan => IRBinaryOperator.GreaterThan,
        IRBinaryOperator.LessThanOrEqual => IRBinaryOperator.GreaterThanOrEqual,
        IRBinaryOperator.GreaterThan => IRBinaryOperator.LessThan,
        IRBinaryOperator.GreaterThanOrEqual => IRBinaryOperator.LessThanOrEqual,
        _ => op,
    };

    /// <summary>An integer constant's value, whatever width it was stored at.</summary>
    public static bool TryConstant(IRExpression expression, out long value)
    {
        value = 0;

        if (Strip(expression) is not IRConstant constant || constant.Value is bool or null)
        {
            return false;
        }

        try
        {
            value = Convert.ToInt64(constant.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
