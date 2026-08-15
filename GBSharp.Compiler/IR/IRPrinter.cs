using System.Globalization;
using System.Text;

namespace GBSharp.Compiler.IR;

/// <summary>
/// Renders IR as text.
/// </summary>
/// <remarks>
/// This is the substrate for compiler tests: a lowering test asserts on printed
/// IR and needs neither a C backend nor a GBDK install. It is also what
/// <c>gbsharp build --emit-ir</c> writes, so a developer can see the shape their
/// code took before the backend touched it.
/// </remarks>
public static class IRPrinter
{
    public static string Print(IRModule module)
    {
        var sb = new StringBuilder();
        sb.Append("module ").Append(module.Name).AppendLine();

        foreach (IRStruct declaration in module.Structs)
        {
            sb.AppendLine();
            sb.Append("struct ").Append(declaration.Name)
              .Append(" (").Append(declaration.SizeInBytes).AppendLine(" bytes)");
            foreach (IRField field in declaration.Fields)
            {
                sb.Append("    ").Append(field.Type.DisplayName).Append(' ').Append(field.Name).AppendLine();
            }
        }

        foreach (IRGlobal global in module.Globals)
        {
            sb.AppendLine();
            sb.Append(global.IsReadOnly ? "rom global " : "global ")
              .Append(BankModifier(global.Bank))
              .Append(global.Type.DisplayName).Append(' ').Append(global.Name);
            if (global.Initializer is not null)
            {
                sb.Append(" = ").Append(PrintExpression(global.Initializer));
            }

            sb.Append("  (").Append(global.Type.SizeInBytes).AppendLine(" bytes)");
        }

        foreach (IRFunction function in module.Functions)
        {
            sb.AppendLine();
            PrintFunction(sb, function, isEntryPoint: ReferenceEquals(function, module.EntryPoint));
        }

        return sb.ToString();
    }

    /// <summary>
    /// A <c>banked(n)</c> prefix, or nothing at all when the declaration is resident.
    /// </summary>
    /// <remarks>
    /// Printing nothing in the resident case keeps the dump for an unbanked
    /// program byte-identical to what it was before banking existed, which is
    /// what makes the IR tests a regression guard rather than a rewrite.
    /// </remarks>
    private static string BankModifier(IRBank bank) =>
        bank.IsResident ? string.Empty : $"banked({bank}) ";

    public static string PrintFunction(IRFunction function)
    {
        var sb = new StringBuilder();
        PrintFunction(sb, function, isEntryPoint: false);
        return sb.ToString();
    }

    private static void PrintFunction(StringBuilder sb, IRFunction function, bool isEntryPoint)
    {
        if (isEntryPoint)
        {
            sb.AppendLine("entrypoint");
        }

        sb.Append("func ").Append(BankModifier(function.Bank)).Append(function.Name).Append('(');
        sb.AppendJoin(", ", function.Parameters.Select(p => $"{p.Type.DisplayName} {p.Name}"));
        sb.Append(") -> ").Append(function.ReturnType.DisplayName).AppendLine();

        foreach (IRLocal local in function.Locals)
        {
            sb.Append("    local ").Append(local.Type.DisplayName).Append(' ').Append(local.Name).AppendLine();
        }

        PrintStatement(sb, function.Body, indent: 1);
    }

    private static void PrintStatement(StringBuilder sb, IRStatement statement, int indent)
    {
        string pad = new(' ', indent * 4);

        switch (statement)
        {
            case IRBlock block:
                sb.Append(pad).AppendLine("{");
                foreach (IRStatement child in block.Statements)
                {
                    PrintStatement(sb, child, indent + 1);
                }

                sb.Append(pad).AppendLine("}");
                break;

            case IRLocalDeclaration declaration:
                sb.Append(pad).Append("decl ").Append(declaration.Local.Name);
                if (declaration.Initializer is not null)
                {
                    sb.Append(" = ").Append(PrintExpression(declaration.Initializer));
                }

                sb.AppendLine();
                break;

            case IRAssign assign:
                sb.Append(pad)
                  .Append(PrintExpression(assign.Target))
                  .Append(" = ")
                  .AppendLine(PrintExpression(assign.Value));
                break;

            case IRCompoundAssign compound:
                sb.Append(pad)
                  .Append(PrintExpression(compound.Target))
                  .Append(' ').Append(OperatorText(compound.Operator)).Append("= ")
                  .AppendLine(PrintExpression(compound.Value));
                break;

            case IRExpressionStatement expression:
                sb.Append(pad).AppendLine(PrintExpression(expression.Expression));
                break;

            case IRIf ifStatement:
                sb.Append(pad).Append("if ").AppendLine(PrintExpression(ifStatement.Condition));
                PrintStatement(sb, ifStatement.Then, indent);
                if (ifStatement.Else is not null)
                {
                    sb.Append(pad).AppendLine("else");
                    PrintStatement(sb, ifStatement.Else, indent);
                }

                break;

            case IRWhile loop:
                sb.Append(pad).Append("while ").AppendLine(PrintExpression(loop.Condition));
                PrintStatement(sb, loop.Body, indent);
                break;

            case IRDoWhile loop:
                sb.Append(pad).AppendLine("do");
                PrintStatement(sb, loop.Body, indent);
                sb.Append(pad).Append("while ").AppendLine(PrintExpression(loop.Condition));
                break;

            case IRFor loop:
                sb.Append(pad).Append("for (");
                sb.AppendJoin("; ", loop.Initializers.Select(InlineStatement));
                sb.Append("; ").Append(loop.Condition is null ? string.Empty : PrintExpression(loop.Condition));
                sb.Append("; ").AppendJoin(", ", loop.Updates.Select(InlineStatement));
                sb.AppendLine(")");
                PrintStatement(sb, loop.Body, indent);
                break;

            case IRSwitch switchStatement:
                sb.Append(pad).Append("switch ").AppendLine(PrintExpression(switchStatement.Value));
                foreach (IRSwitchSection section in switchStatement.Sections)
                {
                    sb.Append(pad).Append("case ")
                      .AppendJoin(", ", section.Values.Select(PrintExpression))
                      .AppendLine(":");
                    PrintStatement(sb, section.Body, indent + 1);
                }

                if (switchStatement.Default is not null)
                {
                    sb.Append(pad).AppendLine("default:");
                    PrintStatement(sb, switchStatement.Default, indent + 1);
                }

                break;

            case IRBreak:
                sb.Append(pad).AppendLine("break");
                break;

            case IRContinue:
                sb.Append(pad).AppendLine("continue");
                break;

            case IRReturn returnStatement:
                sb.Append(pad).Append("return");
                if (returnStatement.Value is not null)
                {
                    sb.Append(' ').Append(PrintExpression(returnStatement.Value));
                }

                sb.AppendLine();
                break;

            default:
                sb.Append(pad).Append("<unknown statement ").Append(statement.GetType().Name).AppendLine(">");
                break;
        }
    }

    private static string InlineStatement(IRStatement statement) => statement switch
    {
        IRLocalDeclaration d => d.Initializer is null
            ? $"decl {d.Local.Name}"
            : $"decl {d.Local.Name} = {PrintExpression(d.Initializer)}",
        IRAssign a => $"{PrintExpression(a.Target)} = {PrintExpression(a.Value)}",
        IRCompoundAssign c => $"{PrintExpression(c.Target)} {OperatorText(c.Operator)}= {PrintExpression(c.Value)}",
        IRExpressionStatement e => PrintExpression(e.Expression),
        _ => "<statement>",
    };

    public static string PrintExpression(IRExpression expression) => expression switch
    {
        IRUnit => "unit",
        IRDefaultValue d => $"default<{d.Type.DisplayName}>",
        IRConstant c => FormatConstant(c),

        // Never dumped: a tileset's bytes are not what anyone reads an IR dump for.
        IRDataBlob b => $"<data: {b.ElementCount} elements, {b.Bytes.Length} bytes>",


        // Elided past a handful of elements: a dump of a tileset is noise, and
        // the length is the part worth reading anyway.
        IRAggregate a => a.Elements.Count <= 8
            ? $"{{ {string.Join(", ", a.Elements.Select(PrintExpression))} }}"
            : $"{{ {string.Join(", ", a.Elements.Take(8).Select(PrintExpression))}, ... }} ({a.Elements.Count} elements)",

        IRLocalRef l => l.Local.Name,
        IRParameterRef p => p.Parameter.Name,
        IRGlobalRef g => g.Global.Name,
        IRFieldAccess f => $"{PrintExpression(f.Target)}.{f.FieldName}",
        IRElementAccess e => $"{PrintExpression(e.Target)}[{PrintExpression(e.Index)}]",
        IRBinary b => $"({PrintExpression(b.Left)} {OperatorText(b.Operator)} {PrintExpression(b.Right)}):{b.Type.DisplayName}",
        IRUnary u => $"({OperatorText(u.Operator)}{PrintExpression(u.Operand)}):{u.Type.DisplayName}",
        IRIncrement i => $"{PrintExpression(i.Target)}{(i.IsDecrement ? "--" : "++")}",
        IRCall c => $"{c.FunctionName}({string.Join(", ", c.Arguments.Select(PrintExpression))})",
        IRNativeCall n => $"native {n.Symbol}({string.Join(", ", n.Arguments.Select(PrintExpression))})",
        IRConvert c => $"convert<{c.Type.DisplayName}>({PrintExpression(c.Operand)})",
        IRConditional c => $"({PrintExpression(c.Condition)} ? {PrintExpression(c.WhenTrue)} : {PrintExpression(c.WhenFalse)})",
        IRAddressOf a => $"&{PrintExpression(a.Operand)}",
        IRDereference d => $"*{PrintExpression(d.Operand)}",
        _ => $"<unknown expression {expression.GetType().Name}>",
    };

    private static string FormatConstant(IRConstant constant) => constant.Value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => constant.Value.ToString() ?? "null",
    };

    private static string OperatorText(IRBinaryOperator op) => op switch
    {
        IRBinaryOperator.Add => "+",
        IRBinaryOperator.Subtract => "-",
        IRBinaryOperator.Multiply => "*",
        IRBinaryOperator.Divide => "/",
        IRBinaryOperator.Remainder => "%",
        IRBinaryOperator.BitwiseAnd => "&",
        IRBinaryOperator.BitwiseOr => "|",
        IRBinaryOperator.BitwiseXor => "^",
        IRBinaryOperator.ShiftLeft => "<<",
        IRBinaryOperator.ShiftRight => ">>",
        IRBinaryOperator.Equal => "==",
        IRBinaryOperator.NotEqual => "!=",
        IRBinaryOperator.LessThan => "<",
        IRBinaryOperator.LessThanOrEqual => "<=",
        IRBinaryOperator.GreaterThan => ">",
        IRBinaryOperator.GreaterThanOrEqual => ">=",
        IRBinaryOperator.LogicalAnd => "&&",
        IRBinaryOperator.LogicalOr => "||",
        _ => "?",
    };

    private static string OperatorText(IRUnaryOperator op) => op switch
    {
        IRUnaryOperator.Negate => "-",
        IRUnaryOperator.LogicalNot => "!",
        IRUnaryOperator.BitwiseNot => "~",
        _ => "?",
    };
}
