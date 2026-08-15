using GBSharp.Compiler.IR;

namespace GBSharp.Compiler.Analysis;

/// <summary>
/// Flat enumeration of the nodes under a statement.
/// </summary>
/// <remarks>
/// <para>
/// This is not the general IR visitor the repository deliberately does not have.
/// It answers one shape of question: "does anything anywhere below here do X?"
/// The structure is irrelevant here, and only membership matters. The cost
/// model does not use it, because cost is a statement about structure: a loop
/// body is multiplied and a conditional's arms are compared, neither of which a
/// flat sequence can express.
/// </para>
/// <para>
/// A node reached through several paths is yielded once per path, which is
/// correct for the membership questions this serves and would be wrong for
/// anything that accumulates. Callers that count must not use this.
/// </para>
/// </remarks>
public static class IRWalk
{
    /// <summary>Every statement at or below one statement, including itself.</summary>
    public static IEnumerable<IRStatement> Statements(IRStatement statement)
    {
        yield return statement;

        switch (statement)
        {
            case IRBlock block:
                foreach (IRStatement child in block.Statements)
                {
                    foreach (IRStatement descendant in Statements(child))
                    {
                        yield return descendant;
                    }
                }

                break;

            case IRIf conditional:
                foreach (IRStatement descendant in Statements(conditional.Then))
                {
                    yield return descendant;
                }

                if (conditional.Else is { } otherwise)
                {
                    foreach (IRStatement descendant in Statements(otherwise))
                    {
                        yield return descendant;
                    }
                }

                break;

            case IRWhile loop:
                foreach (IRStatement descendant in Statements(loop.Body))
                {
                    yield return descendant;
                }

                break;

            case IRDoWhile loop:
                foreach (IRStatement descendant in Statements(loop.Body))
                {
                    yield return descendant;
                }

                break;

            case IRFor loop:
                foreach (IRStatement child in loop.Initializers.Concat(loop.Updates).Append(loop.Body))
                {
                    foreach (IRStatement descendant in Statements(child))
                    {
                        yield return descendant;
                    }
                }

                break;

            case IRSwitch selection:
                foreach (IRSwitchSection section in selection.Sections)
                {
                    foreach (IRStatement descendant in Statements(section.Body))
                    {
                        yield return descendant;
                    }
                }

                if (selection.Default is { } fallback)
                {
                    foreach (IRStatement descendant in Statements(fallback))
                    {
                        yield return descendant;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Every expression at or below one statement, including those in nested
    /// statements.
    /// </summary>
    public static IEnumerable<IRExpression> Expressions(IRStatement statement) =>
        Statements(statement).SelectMany(Own).SelectMany(Descend);

    /// <summary>The expressions a statement holds directly, not counting its children.</summary>
    private static IEnumerable<IRExpression> Own(IRStatement statement)
    {
        switch (statement)
        {
            case IRLocalDeclaration { Initializer: { } value }:
                yield return value;
                break;

            case IRAssign assign:
                yield return assign.Target;
                yield return assign.Value;
                break;

            case IRCompoundAssign compound:
                yield return compound.Target;
                yield return compound.Value;
                break;

            case IRExpressionStatement expression:
                yield return expression.Expression;
                break;

            case IRIf conditional:
                yield return conditional.Condition;
                break;

            case IRWhile loop:
                yield return loop.Condition;
                break;

            case IRDoWhile loop:
                yield return loop.Condition;
                break;

            case IRFor { Condition: { } condition }:
                yield return condition;
                break;

            case IRSwitch selection:
                yield return selection.Value;

                foreach (IRExpression value in selection.Sections.SelectMany(s => s.Values))
                {
                    yield return value;
                }

                break;

            case IRReturn { Value: { } result }:
                yield return result;
                break;
        }
    }

    /// <summary>Every expression at or below one expression, including itself.</summary>
    public static IEnumerable<IRExpression> Descend(IRExpression expression)
    {
        yield return expression;

        foreach (IRExpression child in Children(expression))
        {
            foreach (IRExpression descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The operands an expression holds directly.</summary>
    public static IEnumerable<IRExpression> Children(IRExpression expression) => expression switch
    {
        IRAggregate aggregate => aggregate.Elements,
        IRFieldAccess access => [access.Target],
        IRElementAccess access => [access.Target, access.Index],
        IRBinary binary => [binary.Left, binary.Right],
        IRUnary unary => [unary.Operand],
        IRIncrement increment => [increment.Target],
        IRCall call => call.Arguments,
        IRNativeCall call => call.Arguments,
        IRConvert convert => [convert.Operand],
        IRConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        IRAddressOf address => [address.Operand],
        IRDereference dereference => [dereference.Operand],
        _ => [],
    };
}
