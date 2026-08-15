using System.Globalization;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using GBSharp.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// Lowers one C# method body into GB# IR.
/// </summary>
/// <remarks>
/// Works from Roslyn's <see cref="IOperation"/> tree rather than from syntax.
/// By that point overloads are resolved, implicit conversions are explicit
/// nodes, and constants are folded, so this class only has to decide how each
/// resolved operation should be represented on the target (thesis section 5.1).
/// </remarks>
internal sealed class FunctionLowerer(
    TypeMapper types,
    FrameworkSymbols framework,
    FixedCollections collections,
    DiagnosticBag diagnostics,
    IReadOnlyDictionary<ISymbol, IRGlobal> globals,
    IReadOnlySet<string> knownFunctions,
    AssetBindings assets,
    IReadOnlyDictionary<string, IRBank> functionBanks)
{
    private readonly Dictionary<ISymbol, IRLocal> _locals = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, IRParameter> _parameters = new(SymbolEqualityComparer.Default);
    private readonly List<IRLocal> _declaredLocals = [];

    /// <summary>The bank of the method currently being lowered.</summary>
    private IRBank _currentBank;

    public IRFunction? Lower(IMethodSymbol method, IOperation body)
    {
        Location location = method.Locations.FirstOrDefault() ?? Location.None;

        _currentBank = functionBanks.TryGetValue(NameMangler.ForMethod(method), out IRBank bank)
            ? bank
            : IRBank.Resident;

        IRType? returnType = types.Map(method.ReturnType, location);
        if (returnType is null)
        {
            return null;
        }

        var parameters = new List<IRParameter>();

        // An instance method on a user struct receives the struct by pointer,
        // matching the Player_Update(Player* self) shape of thesis section 9.
        if (!method.IsStatic && method.ContainingType is { TypeKind: TypeKind.Struct })
        {
            IRType? selfType = types.Map(method.ContainingType, location);
            if (selfType is not null)
            {
                parameters.Add(new IRParameter("self", new IRPointerType(selfType)));
            }
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            IRType? parameterType = types.MapDeclaration(
                parameter.Type,
                parameter,
                parameter.Locations.FirstOrDefault());
            if (parameterType is null)
            {
                return null;
            }

            if (parameter.RefKind is RefKind.Ref or RefKind.Out)
            {
                parameterType = new IRPointerType(parameterType);
            }

            // C has no array parameters: 'byte[] map' is a pointer to the first
            // element. Saying so here rather than in the emitter keeps the
            // emitter dumb, and stops it printing 'uint8_t map[0]' from the
            // zero length an undeclared array type carries.
            if (parameterType is IRArrayType arrayParameter)
            {
                parameterType = new IRPointerType(arrayParameter.ElementType);
            }

            ReportIfLargeByValueStruct(parameter, parameterType);

            var irParameter = new IRParameter(parameter.Name, parameterType);
            _parameters[parameter] = irParameter;
            parameters.Add(irParameter);
        }

        IRStatement? loweredBody = LowerBody(body);
        if (loweredBody is null)
        {
            return null;
        }

        IRBlock block = loweredBody as IRBlock ?? new IRBlock([loweredBody]);

        return new IRFunction(
            NameMangler.ForMethod(method),
            returnType,
            parameters,
            _declaredLocals,
            block,
            SourceSpan.FromLocation(location))
        {
            SourceName = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
        };
    }

    /// <summary>
    /// A struct wider than a pointer costs more to pass by value than by ref.
    /// </summary>
    /// <remarks>
    /// SM83 has no register wide enough to hold a struct, so a by-value struct
    /// argument is copied through the stack a byte at a time. A pointer is two
    /// bytes, so any struct larger than that is cheaper to pass by 'ref': the
    /// threshold is the pointer width rather than a tuned number, which is why
    /// it is worth stating rather than guessing.
    /// </remarks>
    private const int LargeStructThreshold = 2;

    private void ReportIfLargeByValueStruct(IParameterSymbol parameter, IRType parameterType)
    {
        if (parameter.RefKind is not RefKind.None || parameterType is not IRStructType structType)
        {
            return;
        }

        if (structType.SizeInBytes <= LargeStructThreshold)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.LargeStruct,
            parameter.Locations.FirstOrDefault(),
            parameter.Name,
            structType.SizeInBytes);
    }

    private IRStatement? LowerBody(IOperation body) => body switch
    {
        // ': this(...)' would need a second constructor, which is refused
        // before a body is ever reached, so only a base call can appear here,
        // and a struct has no base to call.
        IConstructorBodyOperation { Initializer: { } initializer } =>
            Unsupported(initializer, "A constructor initializer"),
        IConstructorBodyOperation { BlockBody: { } constructorBlock } => LowerStatement(constructorBlock),
        IConstructorBodyOperation { ExpressionBody: { } constructorExpression } =>
            LowerStatement(constructorExpression),

        IMethodBodyOperation { BlockBody: { } blockBody } => LowerStatement(blockBody),
        IMethodBodyOperation { ExpressionBody: { } expressionBody } => LowerStatement(expressionBody),
        IBlockOperation block => LowerStatement(block),
        _ => LowerStatement(body),
    };

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lowers one statement and stamps the result with the C# source position
    /// that produced it, for <c>--annotate-source</c> and <c>sourcemap.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The actual lowering lives in <see cref="LowerStatementCore"/>; this
    /// wrapper exists only to attach <see cref="IRStatement.Span"/> in exactly
    /// one place, no matter which of the ~15 operation shapes below produced
    /// the result.
    /// </para>
    /// <para>
    /// A statement that already carries a span is left alone. That is what
    /// keeps a delegating case honest: <c>ILabeledOperation</c> below recurses
    /// into this same wrapper for the operation it labels, which stamps the
    /// label's target with its own precise position; when that already-stamped
    /// result bubbles back up here for the label itself, overwriting it with
    /// the label's span (which includes the label text, not just the
    /// statement) would only make the position less precise, so it is skipped.
    /// An <see cref="IRBlock"/> is skipped for a different reason: it is a
    /// container, not a line of code, so no single span describes it, and its
    /// children were already stamped individually as they were lowered.
    /// </para>
    /// </remarks>
    private IRStatement? LowerStatement(IOperation operation)
    {
        IRStatement? statement = LowerStatementCore(operation);
        if (statement is null or IRBlock || !statement.Span.IsNone)
        {
            return statement;
        }

        SourceSpan span = SourceSpan.FromLocation(Loc(operation));

        return statement switch
        {
            IRLocalDeclaration s => s with { Span = span },
            IRAssign s => s with { Span = span },
            IRCompoundAssign s => s with { Span = span },
            IRExpressionStatement s => s with { Span = span },
            IRIf s => s with { Span = span },
            IRWhile s => s with { Span = span },
            IRDoWhile s => s with { Span = span },
            IRFor s => s with { Span = span },
            IRSwitch s => s with { Span = span },
            IRBreak s => s with { Span = span },
            IRContinue s => s with { Span = span },
            IRReturn s => s with { Span = span },
            _ => statement,
        };
    }

    /// <summary>
    /// The lowering switch itself. Every recursive call in here that reaches a
    /// genuinely different nested statement (a block's children, an
    /// <c>if</c>'s branches) still calls <see cref="LowerStatement"/> (the
    /// wrapper) rather than this method directly, because each of those is its
    /// own statement and needs its own span. Only <see cref="LowerStatement"/>
    /// itself calls in here, to do the actual work once per node.
    /// </summary>
    private IRStatement? LowerStatementCore(IOperation operation)
    {
        switch (operation)
        {
            case IBlockOperation block:
            {
                var statements = new List<IRStatement>();
                foreach (IOperation child in block.Operations)
                {
                    IRStatement? lowered = LowerStatement(child);
                    if (lowered is not null)
                    {
                        statements.Add(lowered);
                    }
                }

                return new IRBlock(statements);
            }

            case IVariableDeclarationGroupOperation group:
            {
                var statements = new List<IRStatement>();
                foreach (IVariableDeclarationOperation declaration in group.Declarations)
                {
                    foreach (IVariableDeclaratorOperation declarator in declaration.Declarators)
                    {
                        IRStatement? lowered = LowerDeclarator(declarator);
                        if (lowered is not null)
                        {
                            statements.Add(lowered);
                        }
                    }
                }

                return statements.Count == 1 ? statements[0] : new IRBlock(statements);
            }

            case IExpressionStatementOperation statement:
                return LowerExpressionStatement(statement.Operation);

            // Roslyn models 'if' and '?:' with the same node; a statement 'if'
            // is the one with no result type.
            case IConditionalOperation ifOperation:
            {
                IRExpression? condition = LowerExpression(ifOperation.Condition, IRPrimitiveType.Bool);
                if (condition is null || ifOperation.WhenTrue is null)
                {
                    return null;
                }

                IRStatement? then = LowerStatement(ifOperation.WhenTrue);
                if (then is null)
                {
                    return null;
                }

                IRStatement? otherwise = ifOperation.WhenFalse is null ? null : LowerStatement(ifOperation.WhenFalse);
                return new IRIf(AsCondition(condition), AsBlock(then), otherwise is null ? null : AsBlock(otherwise));
            }

            case IWhileLoopOperation loop:
                return LowerWhile(loop);

            case IForLoopOperation loop:
                return LowerFor(loop);

            case ISwitchOperation switchOperation:
                return LowerSwitch(switchOperation);

            case IBranchOperation { BranchKind: BranchKind.Break }:
                return new IRBreak();

            case IBranchOperation { BranchKind: BranchKind.Continue }:
                return new IRContinue();

            case IReturnOperation returnOperation:
            {
                if (returnOperation.ReturnedValue is null)
                {
                    return new IRReturn(null);
                }

                IRType? target = types.Map(returnOperation.ReturnedValue.Type, Loc(returnOperation));
                IRExpression? value = LowerExpression(returnOperation.ReturnedValue, target);
                return value is null ? null : new IRReturn(value);
            }

            case IEmptyOperation:
                return new IRBlock([]);

            case ILabeledOperation labeled when labeled.Operation is not null:
                return LowerStatement(labeled.Operation);

            case IForEachLoopOperation:
                return Unsupported(operation, "'foreach'");

            case ITryOperation:
                return Reject(GBDiagnostics.Exceptions, operation);

            case IThrowOperation:
                return Reject(GBDiagnostics.Exceptions, operation);

            case IUsingOperation:
                return Unsupported(operation, "'using' statements");

            case ILockOperation:
                return Unsupported(operation, "'lock'");

            default:
                return Unsupported(operation, DescribeSyntax(operation));
        }
    }

    private IRStatement? LowerDeclarator(IVariableDeclaratorOperation declarator)
    {
        ILocalSymbol symbol = declarator.Symbol;
        Location location = declarator.Syntax.GetLocation();

        // Stamped here, per declarator, rather than left for the
        // IVariableDeclarationGroupOperation case in LowerStatementCore to
        // stamp as a whole: 'int a = 1, b = 2;' is one C# statement but two
        // declarators, each on (conceptually) its own position, and a multi-
        // declarator group returns an IRBlock, which LowerStatement never
        // stamps, so nothing upstream would ever give these a span otherwise.
        SourceSpan span = SourceSpan.FromLocation(location);

        IRType? type = types.MapDeclaration(symbol.Type, symbol, location);
        if (type is null)
        {
            return null;
        }

        // A fixed-size array gets its length from the initializer, because C#
        // types carry no length. GB# needs it to reserve the storage.
        IOperation? initializerValue = declarator.Initializer?.Value;
        if (type is IRArrayType arrayType)
        {
            int? length = TryGetArrayLength(initializerValue);
            if (length is null)
            {
                diagnostics.Report(GBDiagnostics.UnsizedArray, location, symbol.Name);
                return null;
            }

            type = arrayType with { Length = length.Value };
        }

        ReportWideArithmetic(symbol.Type, location);

        var local = new IRLocal(UniqueLocalName(symbol.Name), type);
        _locals[symbol] = local;
        _declaredLocals.Add(local);

        if (initializerValue is null || initializerValue is IArrayCreationOperation { Initializer: null })
        {
            return new IRLocalDeclaration(local, null) { Span = span };
        }

        // 'Point p = new Point(3, 4)' becomes the constructor call alone: the
        // declaration was hoisted to the top of the function, so there is no
        // initializer left to emit and the local is simply the thing the
        // constructor is handed the address of.
        if (IsConstructedStruct(initializerValue))
        {
            return LowerConstructorCall(
                (IObjectCreationOperation)initializerValue,
                new IRLocalRef(local),
                location);
        }

        IRExpression? value = LowerExpression(initializerValue, type);
        return value is null ? null : new IRLocalDeclaration(local, Coerce(value, type)) { Span = span };
    }

    /// <summary>
    /// A C name for a local that no other local in the function uses.
    /// </summary>
    /// <remarks>
    /// C# scopes locals to their block, so two sibling blocks can each declare
    /// an <c>x</c>; the emitter hoists every local to function scope, where the
    /// second declaration would be a duplicate C symbol. The second and later
    /// uses of a name get a numeric suffix. Parameters cannot collide: C#
    /// already rejects a local shadowing a parameter (CS0136).
    /// </remarks>
    private string UniqueLocalName(string name)
    {
        if (_declaredLocals.All(l => l.Name != name))
        {
            return name;
        }

        int suffix = 2;
        while (_declaredLocals.Any(l => l.Name == $"{name}_{suffix}"))
        {
            suffix++;
        }

        return $"{name}_{suffix}";
    }

    private IRStatement? LowerExpressionStatement(IOperation operation)
    {
        switch (operation)
        {
            case ISimpleAssignmentOperation assignment:
                return LowerAssignment(assignment);

            case ICompoundAssignmentOperation compound:
                return LowerCompoundAssignment(compound);

            case IIncrementOrDecrementOperation increment:
            {
                // A property has no storage to increment; rewrite 'p.X++' as
                // 'p.X = p.X + 1' so it becomes a get and a set.
                if (increment.Target is IPropertyReferenceOperation property && !LowersToStorage(property))
                {
                    return LowerPropertyReadModifyWrite(
                        property,
                        increment.Kind == OperationKind.Decrement
                            ? IRBinaryOperator.Subtract
                            : IRBinaryOperator.Add,
                        operand: null,
                        Loc(increment));
                }

                if (WritesToReadOnlyData(increment.Target))
                {
                    return null;
                }

                IRExpression? target = LowerExpression(increment.Target, null);
                if (target is null)
                {
                    return null;
                }

                return new IRExpressionStatement(
                    new IRIncrement(target, increment.Kind == OperationKind.Decrement, target.Type));
            }

            case IInvocationOperation invocation:
            {
                IRExpression? call = LowerInvocation(invocation, null);
                return call is null ? null : new IRExpressionStatement(call);
            }

            default:
            {
                IRExpression? expression = LowerExpression(operation, null);
                return expression is null ? null : new IRExpressionStatement(expression);
            }
        }
    }

    private IRStatement? LowerAssignment(ISimpleAssignmentOperation assignment)
    {
        // Writing a property is a call, not a store: GBDK's setter for a
        // [Native] one, and the setter GB# emitted for a user one.
        if (assignment.Target is IPropertyReferenceOperation property && !LowersToStorage(property))
        {
            IRType? valueType = types.Map(property.Property.Type, Loc(assignment));
            if (valueType is null)
            {
                return null;
            }

            IRExpression? value = LowerExpression(assignment.Value, valueType);
            return value is null
                ? null
                : LowerPropertyWrite(property, Coerce(value, valueType), Loc(assignment));
        }

        if (WritesToReadOnlyData(assignment.Target))
        {
            return null;
        }

        IRExpression? target = LowerExpression(assignment.Target, null);
        if (target is null)
        {
            return null;
        }

        // 'p = new Point(3, 4)' constructs in place, through the address of
        // whatever the left-hand side already names.
        if (IsConstructedStruct(assignment.Value))
        {
            return LowerConstructorCall(
                (IObjectCreationOperation)assignment.Value,
                target,
                Loc(assignment));
        }

        IRExpression? assigned = LowerExpression(assignment.Value, target.Type);
        return assigned is null ? null : new IRAssign(target, Coerce(assigned, target.Type));
    }

    /// <summary>
    /// Reports GBS0056 when a write lands in read-only data, and returns true.
    /// </summary>
    /// <remarks>
    /// C# permits <c>Tiles[0] = 5</c> on a <c>static readonly byte[]</c>:
    /// <c>readonly</c> binds the reference, not the contents. GB# puts that array
    /// in the cartridge as <c>const</c>, where the write simply cannot happen.
    /// Catching it here means the developer reads a GB# diagnostic against their
    /// own C# instead of an SDCC error about generated code they did not write.
    /// </remarks>
    private bool WritesToReadOnlyData(IOperation target)
    {
        IOperation current = target;

        while (true)
        {
            switch (current)
            {
                case IFieldReferenceOperation { Field: { IsReadOnly: true, IsStatic: true } field } reference:
                    diagnostics.Report(
                        GBDiagnostics.WriteToReadOnlyData,
                        reference.Syntax.GetLocation(),
                        field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                    return true;

                case IArrayElementReferenceOperation array:
                    current = array.ArrayReference;
                    continue;

                case IFieldReferenceOperation { Instance: { } instance }:
                    current = instance;
                    continue;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Whether a property reference lowers to storage rather than to a call.
    /// </summary>
    /// <remarks>
    /// A fixed collection's <c>Count</c> or indexer becomes a field or element
    /// access on the emitted struct (a real lvalue in C), so writing one is an
    /// ordinary store and must take the generic assignment path. Every other
    /// property has no storage of its own and has to go through its accessor.
    /// </remarks>
    private static bool LowersToStorage(IPropertyReferenceOperation property) =>
        FixedCollections.IsFixedCollection(property.Instance?.Type);

    /// <summary>
    /// Writes a property, which is a call to its setter rather than a store.
    /// </summary>
    /// <remarks>
    /// Falling through to the generic assignment path instead would lower the
    /// target as a <em>read</em> and emit <c>get_X() = v</c>, which is not an
    /// lvalue in C. So this handles every property that is not backed by
    /// storage, native or not, and the caller must not have lowered the target.
    /// </remarks>
    private IRStatement? LowerPropertyWrite(
        IPropertyReferenceOperation property,
        IRExpression value,
        Location location)
    {
        IMethodSymbol? setter = property.Property.SetMethod;
        ISymbol member = (ISymbol?)setter ?? property.Property;

        var arguments = new List<IRExpression>();
        if (!TryAddReceiver(arguments, property.Instance, member))
        {
            return null;
        }

        if (!TryAddArguments(arguments, property.Arguments))
        {
            return null;
        }

        arguments.Add(value);

        string? symbol = framework.GetNativeSymbol(member);
        if (symbol is not null)
        {
            return new IRExpressionStatement(new IRNativeCall(symbol, arguments, IRPrimitiveType.Void));
        }

        if (setter is not null)
        {
            string mangled = NameMangler.ForMethod(setter);
            if (knownFunctions.Contains(mangled))
            {
                ReportIfBankCrossing(setter, mangled, location);
                return new IRExpressionStatement(new IRCall(mangled, arguments, IRPrimitiveType.Void));
            }
        }

        diagnostics.Report(
            GBDiagnostics.UnresolvedCall,
            location,
            property.Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return null;
    }

    /// <summary>
    /// Rewrites a read-modify-write on a property as a read, an operation and a
    /// write, since there is nothing to modify in place.
    /// </summary>
    private IRStatement? LowerPropertyReadModifyWrite(
        IPropertyReferenceOperation property,
        IRBinaryOperator op,
        IOperation? operand,
        Location location)
    {
        IRExpression? current = LowerPropertyRead(property, null);
        if (current is null)
        {
            return null;
        }

        IRExpression? value;
        if (operand is null)
        {
            // Normalized so the literal carries the width's suffix: the step of
            // a '++' should print as '1U' beside a byte, exactly as a written
            // '+= 1' does. Same source, same C.
            value = new IRConstant(current.Type, IntegerWidth.Normalize(1, current.Type));
        }
        else
        {
            value = LowerExpression(operand, current.Type);
            if (value is null)
            {
                return null;
            }

            value = Coerce(value, op is IRBinaryOperator.ShiftLeft or IRBinaryOperator.ShiftRight
                ? value.Type
                : current.Type);
        }

        return LowerPropertyWrite(
            property,
            Coerce(new IRBinary(op, current, value, current.Type), current.Type),
            location);
    }

    private IRStatement? LowerCompoundAssignment(ICompoundAssignmentOperation compound)
    {
        IRBinaryOperator? op = MapBinaryOperator(compound.OperatorKind);
        if (op is null)
        {
            diagnostics.Report(GBDiagnostics.UnsupportedOperator, Loc(compound), compound.OperatorKind.ToString());
            return null;
        }

        if (compound.Target is IPropertyReferenceOperation property && !LowersToStorage(property))
        {
            return LowerPropertyReadModifyWrite(property, op.Value, compound.Value, Loc(compound));
        }

        if (WritesToReadOnlyData(compound.Target))
        {
            return null;
        }

        IRExpression? target = LowerExpression(compound.Target, null);
        if (target is null)
        {
            return null;
        }

        // The target type is the truncation context: 'x += 1' on a byte stays
        // 8-bit arithmetic even though C# types the addition as int.
        IRExpression? value = LowerExpression(compound.Value, target.Type);
        if (value is null)
        {
            return null;
        }

        IRType valueType = op is IRBinaryOperator.ShiftLeft or IRBinaryOperator.ShiftRight
            ? value.Type
            : target.Type;

        return new IRCompoundAssign(target, op.Value, Coerce(value, valueType));
    }

    private IRStatement? LowerWhile(IWhileLoopOperation loop)
    {
        IRExpression? condition = loop.Condition is null
            ? IRConstant.Bool(true)
            : LowerExpression(loop.Condition, IRPrimitiveType.Bool);

        IRStatement? body = LowerStatement(loop.Body);
        if (condition is null || body is null)
        {
            return null;
        }

        condition = AsCondition(condition);

        // 'until' loops come from VB; in C# this is always false.
        if (loop.ConditionIsUntil)
        {
            condition = new IRUnary(IRUnaryOperator.LogicalNot, condition, IRPrimitiveType.Bool);
        }

        return loop.ConditionIsTop
            ? new IRWhile(condition, AsBlock(body))
            : new IRDoWhile(AsBlock(body), condition);
    }

    private IRStatement? LowerFor(IForLoopOperation loop)
    {
        var initializers = new List<IRStatement>();
        foreach (IOperation before in loop.Before)
        {
            IRStatement? lowered = LowerStatement(before);
            if (lowered is not null)
            {
                initializers.Add(Flatten(lowered));
            }
        }

        IRExpression? condition = loop.Condition is null
            ? null
            : LowerExpression(loop.Condition, IRPrimitiveType.Bool);

        if (loop.Condition is not null && condition is null)
        {
            return null;
        }

        var updates = new List<IRStatement>();
        foreach (IOperation update in loop.AtLoopBottom)
        {
            IRStatement? lowered = LowerStatement(update);
            if (lowered is not null)
            {
                updates.Add(Flatten(lowered));
            }
        }

        IRStatement? body = LowerStatement(loop.Body);
        if (body is null)
        {
            return null;
        }

        return new IRFor(
            initializers,
            condition is null ? null : AsCondition(condition),
            updates,
            AsBlock(body));
    }

    private IRStatement? LowerSwitch(ISwitchOperation switchOperation)
    {
        IRType? valueType = types.Map(switchOperation.Value.Type, Loc(switchOperation));
        IRExpression? value = LowerExpression(switchOperation.Value, valueType);
        if (value is null)
        {
            return null;
        }

        var sections = new List<IRSwitchSection>();
        IRStatement? defaultSection = null;

        foreach (ISwitchCaseOperation switchCase in switchOperation.Cases)
        {
            var statements = new List<IRStatement>();
            foreach (IOperation bodyOperation in switchCase.Body)
            {
                IRStatement? lowered = LowerStatement(bodyOperation);
                if (lowered is not null)
                {
                    statements.Add(lowered);
                }
            }

            var body = new IRBlock(statements);
            var caseValues = new List<IRExpression>();
            bool isDefault = false;

            foreach (ICaseClauseOperation clause in switchCase.Clauses)
            {
                switch (clause)
                {
                    case IDefaultCaseClauseOperation:
                        isDefault = true;
                        break;

                    case ISingleValueCaseClauseOperation single:
                    {
                        IRExpression? caseValue = LowerExpression(single.Value, value.Type);
                        if (caseValue is null)
                        {
                            return null;
                        }

                        caseValues.Add(Coerce(caseValue, value.Type));
                        break;
                    }

                    default:
                        return Unsupported(clause, "This kind of 'case' label");
                }
            }

            if (isDefault)
            {
                defaultSection = body;
            }

            if (caseValues.Count > 0)
            {
                sections.Add(new IRSwitchSection(caseValues, body));
            }
        }

        return new IRSwitch(value, sections, defaultSection);
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lowers an expression.
    /// </summary>
    /// <param name="context">
    /// The type the result will be stored into or truncated to, when that is
    /// known. This is what allows <c>(byte)(a + b)</c> to be computed in 8 bits:
    /// truncation to a byte makes the narrow result provably equal to the wide
    /// one. Null means no truncation is guaranteed, so no narrowing is assumed.
    /// </param>
    private IRExpression? LowerExpression(IOperation? operation, IRType? context)
    {
        if (operation is null)
        {
            return null;
        }

        if (operation is IParenthesizedOperation parenthesized)
        {
            return LowerExpression(parenthesized.Operand, context);
        }

        // Constants fold here, which also resolves enum members and const fields.
        if (operation.ConstantValue is { HasValue: true, Value: { } constant })
        {
            return MakeConstant(constant, operation, context);
        }

        switch (operation)
        {
            case ILocalReferenceOperation local:
                return _locals.TryGetValue(local.Local, out IRLocal? irLocal)
                    ? new IRLocalRef(irLocal)
                    : Fail(operation, $"local '{local.Local.Name}' was used before it was declared");

            case IParameterReferenceOperation parameter:
            {
                if (!_parameters.TryGetValue(parameter.Parameter, out IRParameter? irParameter))
                {
                    return Fail(operation, $"parameter '{parameter.Parameter.Name}' is unknown");
                }

                IRExpression reference = new IRParameterRef(irParameter);

                // A 'ref' parameter is a pointer on the target; reading it reads
                // through. Ref-ness has to come from the C# symbol rather than
                // from the IR type, because an array parameter is a pointer too
                // and that one *is* the value: 'data[i]' indexes it directly.
                bool isByRef = parameter.Parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In;

                return isByRef && irParameter.Type is IRPointerType pointer
                    ? new IRDereference(reference, pointer.PointeeType)
                    : reference;
            }

            case IFieldReferenceOperation field:
                return LowerFieldReference(field);

            case IPropertyReferenceOperation property:
                return LowerPropertyRead(property, context);

            case IInvocationOperation invocation:
                return LowerInvocation(invocation, context);

            case IArrayElementReferenceOperation element:
                return LowerArrayElement(element);

            case IBinaryOperation binary:
                return LowerBinary(binary, context);

            case IUnaryOperation unary:
                return LowerUnary(unary, context);

            case IConversionOperation conversion:
                return LowerConversion(conversion, context);

            case IInstanceReferenceOperation instance:
                return LowerInstanceReference(instance);

            // The same node kind as a statement 'if'; here it has a value.
            case IConditionalOperation conditional when conditional.WhenFalse is not null:
            {
                IRType? type = context ?? types.Map(conditional.Type, Loc(conditional));
                if (type is null)
                {
                    return null;
                }

                IRExpression? condition = LowerExpression(conditional.Condition, IRPrimitiveType.Bool);
                IRExpression? whenTrue = LowerExpression(conditional.WhenTrue, type);
                IRExpression? whenFalse = LowerExpression(conditional.WhenFalse, type);

                if (condition is null || whenTrue is null || whenFalse is null)
                {
                    return null;
                }

                return new IRConditional(
                    AsCondition(condition),
                    Coerce(whenTrue, type),
                    Coerce(whenFalse, type),
                    type);
            }

            case IDefaultValueOperation:
            {
                IRType? type = context ?? types.Map(operation.Type, Loc(operation));
                return type is null ? null : DefaultOf(type);
            }

            case IObjectCreationOperation creation:
                return LowerObjectCreation(creation);

            case IIsTypeOperation or ITypeOfOperation:
                return RejectExpression(GBDiagnostics.UnsupportedConstruct, operation, "Runtime type inspection");

            case IAnonymousFunctionOperation or IDelegateCreationOperation:
                return RejectExpression(GBDiagnostics.DelegatesAndEvents, operation, "A lambda or delegate");

            case IAwaitOperation:
                return RejectExpression(GBDiagnostics.AsyncAwait, operation);

            default:
                Unsupported(operation, DescribeSyntax(operation));
                return null;
        }
    }

    private IRExpression? LowerInstanceReference(IInstanceReferenceOperation instance)
    {
        // 'this' inside a struct method is the 'self' pointer.
        if (instance.Type is { TypeKind: TypeKind.Struct })
        {
            IRType? type = types.Map(instance.Type, Loc(instance));
            if (type is null)
            {
                return null;
            }

            if (IsErasedHandle(instance.Type))
            {
                return IRUnit.Instance;
            }

            return new IRDereference(new IRParameterRef(new IRParameter("self", new IRPointerType(type))), type);
        }

        return IRUnit.Instance;
    }

    private IRExpression? LowerFieldReference(IFieldReferenceOperation field)
    {
        if (framework.IsNativeIdentity(field.Field))
        {
            return IRUnit.Instance;
        }

        // An asset reaching here is being used as a value. It has no value: it
        // names several ROM tables, and the only thing that can consume it is a
        // loader, where TryExpandAsset intercepts it before this point.
        if (assets.IsAsset(field.Field))
        {
            diagnostics.Report(
                GBDiagnostics.AssetUsedAsValue,
                Loc(field),
                field.Field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return null;
        }

        if (field.Field.IsStatic)
        {
            if (!globals.TryGetValue(field.Field, out IRGlobal? global))
            {
                return Fail(field, $"static field '{field.Field.Name}' has no storage");
            }

            // Data in another bank is only at that address while its bank is
            // mapped. Reading it from code that has not switched gets whatever
            // else lives there, which is the kind of failure that shows up as
            // corrupt graphics rather than as a crash, so it is refused here.
            if (!global.Bank.IsResident && global.Bank != _currentBank)
            {
                return RejectExpression(
                    GBDiagnostics.BankedDataAccess,
                    field,
                    field.Field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    global.Bank.ToString());
            }

            return new IRGlobalRef(global);
        }

        IRExpression? instance = LowerExpression(field.Instance, null);
        if (instance is null)
        {
            return null;
        }

        IRType? type = types.Map(field.Field.Type, Loc(field));
        return type is null ? null : new IRFieldAccess(instance, field.Field.Name, type);
    }

    private IRExpression? LowerArrayElement(IArrayElementReferenceOperation element)
    {
        IRExpression? target = LowerExpression(element.ArrayReference, null);
        if (target is null)
        {
            return null;
        }

        if (element.Indices.Length != 1)
        {
            return RejectExpression(GBDiagnostics.UnsupportedConstruct, element, "Multi-dimensional arrays");
        }

        IRExpression? index = LowerExpression(element.Indices[0], IRPrimitiveType.U8);
        if (index is null)
        {
            return null;
        }

        IRType elementType = target.Type is IRArrayType array
            ? array.ElementType
            : types.Map(element.Type, Loc(element)) ?? IRPrimitiveType.U8;

        return new IRElementAccess(target, index, elementType);
    }

    private IRExpression? LowerObjectCreation(IObjectCreationOperation creation)
    {
        if (creation.Type is { TypeKind: not TypeKind.Struct })
        {
            diagnostics.Report(
                GBDiagnostics.ReferenceTypeAllocation,
                Loc(creation),
                creation.Type.ToDisplayString());
            return null;
        }

        // 'new SomeStruct()' with no arguments is zero-initialisation.
        if (creation.Arguments.Length == 0)
        {
            IRType? type = types.Map(creation.Type, Loc(creation));
            return type is null ? null : DefaultOf(type);
        }

        // Reaching here means the 'new' is in a position that has no storage to
        // construct into: an argument, a return, an operand. A constructor
        // writes through a pointer to an existing struct, so GB# would have to
        // invent a temporary, and a temporary the developer did not write is
        // stack they cannot see. Naming the two positions that do work is more
        // use than naming the construct.
        diagnostics.Report(
            GBDiagnostics.ConstructorPosition,
            Loc(creation),
            creation.Type?.Name ?? "struct");
        return null;
    }

    /// <summary>
    /// Whether an operation is a <c>new T(...)</c> on a struct that GB# lowers
    /// through a constructor call rather than to a value.
    /// </summary>
    private static bool IsConstructedStruct(IOperation? operation) =>
        operation is IObjectCreationOperation { Arguments.Length: > 0, Type.TypeKind: TypeKind.Struct };

    /// <summary>
    /// Calls a constructor, handing it the address of the storage it fills.
    /// </summary>
    /// <remarks>
    /// The same shape as any other instance member: <c>Point__ctor(&amp;p, 3, 4)</c>
    /// against <c>Point__ctor(Point* self, ...)</c>. Nothing is inlined and no
    /// temporary is created, so the cost of a constructor is one visible call.
    /// </remarks>
    private IRStatement? LowerConstructorCall(
        IObjectCreationOperation creation,
        IRExpression target,
        Location location)
    {
        if (creation.Constructor is not { } constructor)
        {
            diagnostics.Report(
                GBDiagnostics.InternalError,
                location,
                $"no constructor symbol for '{creation.Type?.Name}'");
            return null;
        }

        var arguments = new List<IRExpression> { AddressOf(target) };

        if (!TryAddArguments(arguments, creation.Arguments))
        {
            return null;
        }

        string mangled = NameMangler.ForMethod(constructor);
        if (!knownFunctions.Contains(mangled))
        {
            diagnostics.Report(
                GBDiagnostics.UnresolvedCall,
                location,
                constructor.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return null;
        }

        ReportIfBankCrossing(constructor, mangled, location);
        return new IRExpressionStatement(new IRCall(mangled, arguments, IRPrimitiveType.Void));
    }

    /// <summary>
    /// Reads a property. Framework properties become native calls; user
    /// properties become calls to their lowered getter.
    /// </summary>
    private IRExpression? LowerPropertyRead(IPropertyReferenceOperation property, IRType? context)
    {
        if (property.Instance is not null && FixedCollections.IsFixedCollection(property.Instance.Type))
        {
            return LowerFixedCollectionProperty(property);
        }

        IMethodSymbol? getter = property.Property.GetMethod;

        if (framework.IsNativeIdentity((ISymbol?)getter ?? property.Property) ||
            framework.IsNativeIdentity(property.Property))
        {
            return LowerIdentity(property);
        }

        IRType? type = types.Map(property.Property.Type, Loc(property));
        if (type is null)
        {
            return null;
        }

        string? symbol = framework.GetNativeSymbol((ISymbol?)getter ?? property.Property);
        var arguments = new List<IRExpression>();

        if (!TryAddReceiver(arguments, property.Instance, (ISymbol?)getter ?? property.Property))
        {
            return null;
        }

        if (!TryAddArguments(arguments, property.Arguments))
        {
            return null;
        }

        if (symbol is not null)
        {
            return Contextualize(new IRNativeCall(symbol, arguments, type), context);
        }

        if (getter is not null && knownFunctions.Contains(NameMangler.ForMethod(getter)))
        {
            return Contextualize(new IRCall(NameMangler.ForMethod(getter), arguments, type), context);
        }

        diagnostics.Report(
            GBDiagnostics.UnresolvedCall,
            Loc(property),
            property.Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return null;
    }

    /// <summary>
    /// Reads a member of a specialised fixed collection.
    /// </summary>
    /// <remarks>
    /// <c>Length</c> and <c>Capacity</c> fold to constants; <c>Count</c> and the
    /// indexer become a field or element access on the emitted struct. Nothing
    /// generic remains, and nothing is called.
    /// </remarks>
    private IRExpression? LowerFixedCollectionProperty(IPropertyReferenceOperation property)
    {
        IRExpression? target = LowerExpression(property.Instance, null);
        if (target is null)
        {
            return null;
        }

        FixedCollectionInfo? info = collections.Lookup(target.Type);
        if (info is null)
        {
            diagnostics.Report(
                GBDiagnostics.CapacityRequired,
                Loc(property),
                property.Instance?.Type?.ToDisplayString() ?? property.Property.Name);
            return null;
        }

        switch (property.Property.Name)
        {
            case "Length" or "Capacity":
                return IRConstant.U8((byte)info.Capacity);

            case "Count":
                return new IRFieldAccess(target, FixedCollectionInfo.CountField, IRPrimitiveType.U8);

            // The indexer.
            case "this[]":
            {
                if (property.Arguments.Length != 1)
                {
                    return RejectExpression(GBDiagnostics.UnsupportedConstruct, property, "This indexer");
                }

                IRExpression? index = LowerExpression(property.Arguments[0].Value, IRPrimitiveType.U8);
                if (index is null)
                {
                    return null;
                }

                var storage = new IRFieldAccess(target, FixedCollectionInfo.ItemsField, info.StorageType);
                return new IRElementAccess(storage, index, info.ElementType);
            }

            default:
                return RejectExpression(
                    GBDiagnostics.UnsupportedConstruct,
                    property,
                    $"'{property.Property.Name}' on a fixed collection");
        }
    }

    /// <summary>
    /// Calls one of the generated fixed-list operations, passing the collection
    /// by address so it is mutated in place rather than copied.
    /// </summary>
    private IRExpression? LowerFixedCollectionCall(IInvocationOperation invocation)
    {
        IRExpression? target = LowerExpression(invocation.Instance, null);
        if (target is null)
        {
            return null;
        }

        FixedCollectionInfo? info = collections.Lookup(target.Type);
        if (info is null)
        {
            diagnostics.Report(
                GBDiagnostics.CapacityRequired,
                Loc(invocation),
                invocation.Instance?.Type?.ToDisplayString() ?? invocation.TargetMethod.Name);
            return null;
        }

        var arguments = new List<IRExpression> { new IRAddressOf(target) };

        switch (invocation.TargetMethod.Name)
        {
            case "Add":
            {
                IRExpression? item = LowerExpression(invocation.Arguments[0].Value, info.ElementType);
                if (item is null)
                {
                    return null;
                }

                arguments.Add(item);
                return new IRCall(info.AddFunction, arguments, IRPrimitiveType.Bool);
            }

            case "RemoveAt":
            {
                IRExpression? index = LowerExpression(invocation.Arguments[0].Value, IRPrimitiveType.U8);
                if (index is null)
                {
                    return null;
                }

                arguments.Add(index);
                return new IRCall(info.RemoveAtFunction, arguments, IRPrimitiveType.Void);
            }

            case "Clear":
                return new IRCall(info.ClearFunction, arguments, IRPrimitiveType.Void);

            default:
                return RejectExpression(
                    GBDiagnostics.UnsupportedConstruct,
                    invocation,
                    $"'{invocation.TargetMethod.Name}' on a fixed collection");
        }
    }

    /// <summary>
    /// Lowers a <c>[NativeIdentity]</c> member: it produces its single argument,
    /// or its receiver, or nothing at all.
    /// </summary>
    private IRExpression? LowerIdentity(IPropertyReferenceOperation property)
    {
        if (property.Arguments.Length == 1)
        {
            IRType? indexType = types.Map(property.Arguments[0].Parameter?.Type, Loc(property));
            return LowerExpression(property.Arguments[0].Value, indexType ?? IRPrimitiveType.U8);
        }

        if (property.Arguments.Length == 0)
        {
            return property.Instance is null
                ? IRUnit.Instance
                : LowerExpression(property.Instance, null);
        }

        diagnostics.Report(
            GBDiagnostics.NativeSignatureInvalid,
            Loc(property),
            property.Property.Name,
            "a [NativeIdentity] member must carry through exactly one value");
        return null;
    }

    private IRExpression? LowerInvocation(IInvocationOperation invocation, IRType? context)
    {
        if (invocation.Instance is not null && FixedCollections.IsFixedCollection(invocation.Instance.Type))
        {
            return LowerFixedCollectionCall(invocation);
        }

        IMethodSymbol target = invocation.TargetMethod;
        Location location = Loc(invocation);

        // Checked before the return type is mapped. A LINQ call returns
        // IEnumerable<T>, so without this the developer is told their type is
        // unsupported rather than that LINQ is, which names the symptom instead
        // of the cause.
        if (SubsetRules.IsLinq(target))
        {
            return RejectExpression(GBDiagnostics.Linq, invocation);
        }

        IRType? returnType = types.Map(target.ReturnType, location);
        if (returnType is null)
        {
            return null;
        }

        if (framework.IsNativeIdentity(target))
        {
            IArgumentOperation? single = invocation.Arguments.Length == 1 ? invocation.Arguments[0] : null;
            if (single is not null)
            {
                return LowerExpression(single.Value, returnType);
            }

            return invocation.Instance is null ? IRUnit.Instance : LowerExpression(invocation.Instance, null);
        }

        var arguments = new List<IRExpression>();
        if (!TryAddReceiver(arguments, invocation.Instance, target))
        {
            return null;
        }

        if (!TryAddArguments(arguments, invocation.Arguments))
        {
            return null;
        }

        string? symbol = framework.GetNativeSymbol(target);
        if (symbol is not null)
        {
            return Contextualize(new IRNativeCall(symbol, arguments, returnType), context);
        }

        string mangled = NameMangler.ForMethod(target);
        if (knownFunctions.Contains(mangled))
        {
            ReportIfBankCrossing(target, mangled, location);
            return Contextualize(new IRCall(mangled, arguments, returnType), context);
        }

        diagnostics.Report(
            GBDiagnostics.UnresolvedCall,
            location,
            target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return null;
    }

    /// <summary>
    /// Says so at the call site when reaching the callee changes the mapped bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported where the call is written rather than where the callee is
    /// declared, because the cost is paid by the caller and the caller is who
    /// can move it out of a frame loop.
    /// </para>
    /// <para>
    /// Two functions in the same bank reach each other without a switch, so only
    /// a difference matters. An automatic placement is reported too: the build
    /// has not chosen yet, but the trampoline is generated either way.
    /// </para>
    /// </remarks>
    private void ReportIfBankCrossing(IMethodSymbol target, string mangled, Location location)
    {
        if (!functionBanks.TryGetValue(mangled, out IRBank calleeBank) || calleeBank.IsResident)
        {
            return;
        }

        if (calleeBank == _currentBank)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.BankedCall,
            location,
            target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            calleeBank.ToString());
    }

    /// <summary>Adds a receiver, dropping it when it erased to nothing.</summary>
    /// <param name="callee">
    /// The member being called. Needed because whether the receiver is passed
    /// by value or by address is a property of the callee's signature, not of
    /// the receiver expression.
    /// </param>
    private bool TryAddReceiver(List<IRExpression> arguments, IOperation? instance, ISymbol? callee)
    {
        if (instance is null)
        {
            return true;
        }

        IRExpression? lowered = LowerExpression(instance, null);
        if (lowered is null)
        {
            return false;
        }

        if (lowered is IRUnit)
        {
            return true;
        }

        arguments.Add(TakesSelfPointer(callee) ? AddressOf(lowered) : lowered);
        return true;
    }

    /// <summary>
    /// Whether calling <paramref name="callee"/> passes the receiver as the
    /// <c>self</c> pointer that <see cref="Lower"/> declares.
    /// </summary>
    /// <remarks>
    /// Deliberately the exact inverse of the parameter added when lowering the
    /// declaration, and written to look like it: the two have to agree, or the
    /// call site hands a struct to a function that reads a pointer. A
    /// <c>[Native]</c> member is excluded because its signature is GBDK's
    /// rather than one GB# emitted, and an erased handle is excluded because
    /// its receiver disappears rather than being passed at all.
    /// </remarks>
    private bool TakesSelfPointer(ISymbol? callee)
    {
        if (callee is not { IsStatic: false } ||
            callee.ContainingType is not { TypeKind: TypeKind.Struct } containing ||
            IsErasedHandle(containing))
        {
            return false;
        }

        return !IsNativeMember(callee);
    }

    /// <summary>
    /// Whether a member maps straight to a C symbol, following an accessor back
    /// to the property that carries the attribute.
    /// </summary>
    private bool IsNativeMember(ISymbol member)
    {
        if (framework.GetNativeSymbol(member) is not null || framework.IsNativeIdentity(member))
        {
            return true;
        }

        return member is IMethodSymbol { AssociatedSymbol: { } associated } &&
            (framework.GetNativeSymbol(associated) is not null || framework.IsNativeIdentity(associated));
    }

    /// <summary>
    /// The address of a receiver.
    /// </summary>
    /// <remarks>
    /// A receiver that is already a dereference (<c>this</c> inside a struct
    /// method, or a <c>ref</c> parameter) hands back the pointer it started
    /// from rather than growing an <c>&amp;(*p)</c> round trip that means the
    /// same thing and reads worse. The generated C is meant to be read.
    /// </remarks>
    private static IRExpression AddressOf(IRExpression receiver) =>
        receiver is IRDereference dereference ? dereference.Operand : new IRAddressOf(receiver);

    private bool TryAddArguments(List<IRExpression> arguments, IEnumerable<IArgumentOperation> source)
    {
        foreach (IArgumentOperation argument in source)
        {
            // An asset contributes several arguments rather than one: the
            // pointers to its ROM tables and the sizes that go with them. This
            // is IRUnit's mechanism running the other way: that one removes an
            // argument so 'Sprites[0].X' loses its receiver, this one expands
            // one into the eight a loader needs.
            if (TryExpandAsset(arguments, argument))
            {
                continue;
            }

            IRType? parameterType = argument.Parameter is null
                ? null
                : types.Map(argument.Parameter.Type, Loc(argument));

            // A 'ref' argument passes the address rather than the value.
            if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
            {
                IRExpression? target = LowerExpression(argument.Value, parameterType);
                if (target is null)
                {
                    return false;
                }

                arguments.Add(new IRAddressOf(target));
                continue;
            }

            IRExpression? lowered = LowerExpression(argument.Value, parameterType);
            if (lowered is null)
            {
                return false;
            }

            if (lowered is IRUnit)
            {
                continue;
            }

            arguments.Add(parameterType is null ? lowered : Coerce(lowered, parameterType));
        }

        return true;
    }

    /// <summary>
    /// Expands an asset argument into the loader's real parameter list.
    /// </summary>
    /// <returns>True if the argument was an asset and has been handled.</returns>
    private bool TryExpandAsset(List<IRExpression> arguments, IArgumentOperation argument)
    {
        if (argument.Value is not IFieldReferenceOperation { Field: { } field } || !assets.IsAsset(field))
        {
            return false;
        }

        if (assets.For(field) is { } binding)
        {
            arguments.AddRange(binding.Arguments);
        }

        // A field that is an asset but has no binding failed to convert, and the
        // converter has already said why. Swallowing it here keeps one bad image
        // from also producing a confusing second error about the call.
        return true;
    }

    private IRExpression? LowerConversion(IConversionOperation conversion, IRType? context)
    {
        ITypeSymbol? targetSymbol = conversion.Type;

        if (targetSymbol is { SpecialType: SpecialType.System_Object } ||
            targetSymbol is { TypeKind: TypeKind.Interface })
        {
            diagnostics.Report(
                GBDiagnostics.Boxing,
                Loc(conversion),
                conversion.Operand.Type?.ToDisplayString() ?? "value",
                targetSymbol.ToDisplayString());
            return null;
        }

        IRType? target = types.Map(targetSymbol, Loc(conversion));
        if (target is null)
        {
            return null;
        }

        // A widening C# inserted, where the consumer has already said it wants
        // the value narrow: drop it rather than round-tripping through a wider
        // type. This is what keeps an array index a byte instead of promoting it
        // to int and casting straight back.
        if (conversion.Conversion is { IsNumeric: true, IsImplicit: true } &&
            IntegerWidth.IsWidening(conversion.Operand.Type, conversion.Type) &&
            context is IRPrimitiveType narrowContext &&
            IntegerWidth.AsPrimitive(target) is { } wideTarget &&
            narrowContext.WidthInBits <= wideTarget.WidthInBits)
        {
            return LowerExpression(conversion.Operand, context);
        }

        // The conversion target is the truncation context for whatever is
        // inside it. This is the hook that keeps '(byte)(a + b)' 8-bit.
        IRExpression? operand = LowerExpression(conversion.Operand, target);
        if (operand is null)
        {
            return null;
        }

        return Coerce(operand, target);
    }

    private IRExpression? LowerBinary(IBinaryOperation binary, IRType? context)
    {
        IRBinaryOperator? mapped = MapBinaryOperator(binary.OperatorKind);
        if (mapped is null)
        {
            diagnostics.Report(GBDiagnostics.UnsupportedOperator, Loc(binary), binary.OperatorKind.ToString());
            return null;
        }

        IRBinaryOperator op = mapped.Value;

        if (op is IRBinaryOperator.LogicalAnd or IRBinaryOperator.LogicalOr)
        {
            IRExpression? left = LowerExpression(binary.LeftOperand, IRPrimitiveType.Bool);
            IRExpression? right = LowerExpression(binary.RightOperand, IRPrimitiveType.Bool);
            if (left is null || right is null)
            {
                return null;
            }

            return new IRBinary(op, AsCondition(left), AsCondition(right), IRPrimitiveType.Bool);
        }

        IOperation leftSource = StripWidening(binary.LeftOperand);
        IOperation rightSource = StripWidening(binary.RightOperand);

        if (op.IsComparison())
        {
            IRPrimitiveType operandType = ChooseOperandType(leftSource, rightSource);
            IRExpression? left = LowerExpression(leftSource, operandType);
            IRExpression? right = LowerExpression(rightSource, operandType);
            if (left is null || right is null)
            {
                return null;
            }

            return new IRBinary(op, Coerce(left, operandType), Coerce(right, operandType), IRPrimitiveType.Bool);
        }

        // Shifts are asymmetric: the result takes the left operand's width and
        // the shift amount is just a count.
        if (op is IRBinaryOperator.ShiftLeft or IRBinaryOperator.ShiftRight)
        {
            IRPrimitiveType valueType = OperandType(leftSource) ?? IRPrimitiveType.U8;
            IRPrimitiveType resultType = ResolveArithmeticWidth(binary, op, valueType, context);

            IRExpression? value = LowerExpression(leftSource, resultType);
            IRExpression? amount = LowerExpression(rightSource, IRPrimitiveType.U8);
            if (value is null || amount is null)
            {
                return null;
            }

            return new IRBinary(op, Coerce(value, resultType), amount, resultType);
        }

        IRPrimitiveType natural = ChooseOperandType(leftSource, rightSource);
        IRPrimitiveType result = ResolveArithmeticWidth(binary, op, natural, context);

        ReportExpensiveArithmetic(binary, op, result);

        IRExpression? loweredLeft = LowerExpression(leftSource, result);
        IRExpression? loweredRight = LowerExpression(rightSource, result);
        if (loweredLeft is null || loweredRight is null)
        {
            return null;
        }

        return new IRBinary(op, Coerce(loweredLeft, result), Coerce(loweredRight, result), result);
    }

    /// <summary>
    /// Decides the width an arithmetic operation actually runs at.
    /// </summary>
    /// <remarks>
    /// See <see cref="IntegerWidth"/> for why this is not simply the C# result
    /// type. Anything that stays wider than its operands is reported, so a
    /// developer never pays for 16- or 32-bit arithmetic without being told.
    /// </remarks>
    private IRPrimitiveType ResolveArithmeticWidth(
        IBinaryOperation binary,
        IRBinaryOperator op,
        IRPrimitiveType natural,
        IRType? context)
    {
        IRPrimitiveType declared = IntegerWidth.AsPrimitive(types.Map(binary.Type, Loc(binary))) ?? natural;

        if (natural.WidthInBits >= declared.WidthInBits)
        {
            return declared;
        }

        // The result is about to be truncated anyway, and this operator is
        // congruent modulo the narrow width, so the narrow result is identical.
        if (context is IRPrimitiveType truncation &&
            truncation.WidthInBits <= natural.WidthInBits &&
            op.IsCongruentModuloWidth())
        {
            return natural;
        }

        // The result cannot exceed the operands, so widening buys nothing.
        if (!natural.IsSigned && op.ResultFitsOperandWidth())
        {
            return natural;
        }

        if (declared.WidthInBits >= 32)
        {
            diagnostics.Report(
                GBDiagnostics.Int32Arithmetic,
                Loc(binary),
                binary.Type?.ToDisplayString() ?? "int");
        }
        else
        {
            diagnostics.Report(
                GBDiagnostics.WideningArithmetic,
                Loc(binary),
                DescribeOperator(op),
                declared.WidthInBits,
                declared.DisplayName);
        }

        return declared;
    }

    private void ReportExpensiveArithmetic(IBinaryOperation binary, IRBinaryOperator op, IRPrimitiveType type)
    {
        // SM83 has neither a multiply nor a divide instruction. At 8 bits SDCC
        // emits a compact helper; at 16 it is markedly worse, and worth saying.
        if (type.WidthInBits < 16)
        {
            return;
        }

        switch (op)
        {
            case IRBinaryOperator.Multiply:
                diagnostics.Report(GBDiagnostics.ExpensiveMultiplication, Loc(binary), type.DisplayName);
                break;

            case IRBinaryOperator.Divide:
                diagnostics.Report(GBDiagnostics.ExpensiveDivision, Loc(binary), "Division", type.DisplayName);
                break;

            case IRBinaryOperator.Remainder:
                diagnostics.Report(GBDiagnostics.ExpensiveDivision, Loc(binary), "Remainder", type.DisplayName);
                break;
        }
    }

    private IRExpression? LowerUnary(IUnaryOperation unary, IRType? context)
    {
        IRUnaryOperator op = unary.OperatorKind switch
        {
            UnaryOperatorKind.Minus => IRUnaryOperator.Negate,
            UnaryOperatorKind.Not => IRUnaryOperator.LogicalNot,
            UnaryOperatorKind.BitwiseNegation => IRUnaryOperator.BitwiseNot,
            UnaryOperatorKind.Plus => IRUnaryOperator.Negate,
            _ => IRUnaryOperator.LogicalNot,
        };

        if (unary.OperatorKind == UnaryOperatorKind.Plus)
        {
            return LowerExpression(unary.Operand, context);
        }

        if (unary.OperatorKind is not (UnaryOperatorKind.Minus or UnaryOperatorKind.Not or UnaryOperatorKind.BitwiseNegation))
        {
            diagnostics.Report(GBDiagnostics.UnsupportedOperator, Loc(unary), unary.OperatorKind.ToString());
            return null;
        }

        if (op == IRUnaryOperator.LogicalNot)
        {
            IRExpression? condition = LowerExpression(unary.Operand, IRPrimitiveType.Bool);
            return condition is null ? null : new IRUnary(op, AsCondition(condition), IRPrimitiveType.Bool);
        }

        IOperation source = StripWidening(unary.Operand);
        IRPrimitiveType type = context as IRPrimitiveType
            ?? OperandType(source)
            ?? IRPrimitiveType.U8;

        IRExpression? operand = LowerExpression(source, type);
        return operand is null ? null : new IRUnary(op, Coerce(operand, type), type);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Picks the width two operands should be compared or combined at, letting a
    /// constant adopt the other operand's type when it fits.
    /// </summary>
    private IRPrimitiveType ChooseOperandType(IOperation left, IOperation right)
    {
        IRPrimitiveType? leftType = OperandType(left);
        IRPrimitiveType? rightType = OperandType(right);

        bool leftIsConstant = left.ConstantValue.HasValue;
        bool rightIsConstant = right.ConstantValue.HasValue;

        if (leftIsConstant && !rightIsConstant && rightType is not null)
        {
            return IntegerWidth.Fits(left.ConstantValue.Value, rightType)
                ? rightType
                : IntegerWidth.Wider(rightType, IntegerWidth.ForConstant(left.ConstantValue.Value) ?? rightType);
        }

        if (rightIsConstant && !leftIsConstant && leftType is not null)
        {
            return IntegerWidth.Fits(right.ConstantValue.Value, leftType)
                ? leftType
                : IntegerWidth.Wider(leftType, IntegerWidth.ForConstant(right.ConstantValue.Value) ?? leftType);
        }

        if (leftType is not null && rightType is not null)
        {
            return IntegerWidth.Wider(leftType, rightType);
        }

        return leftType ?? rightType ?? IRPrimitiveType.U8;
    }

    /// <summary>
    /// The width an operand will actually be evaluated at.
    /// </summary>
    /// <remarks>
    /// Not simply its C# type. A nested expression like <c>frame &gt;&gt; 3</c>
    /// is typed <c>int</c> by C#, but GB# will lower it to 8 bits; asking the
    /// declared type here would widen the enclosing operation back to 32 bits
    /// and undo the narrowing one level down. So this recurses to find the width
    /// the sub-expression will really have.
    /// </remarks>
    private IRPrimitiveType? OperandType(IOperation operation)
    {
        operation = StripWidening(operation);

        if (!operation.ConstantValue.HasValue && operation is IBinaryOperation nested)
        {
            IRBinaryOperator? op = MapBinaryOperator(nested.OperatorKind);

            if (op is null)
            {
                return IntegerWidth.AsPrimitive(types.Map(operation.Type, null));
            }

            if (op.Value.IsComparison() ||
                op.Value is IRBinaryOperator.LogicalAnd or IRBinaryOperator.LogicalOr)
            {
                return IRPrimitiveType.Bool;
            }

            // A shift takes its width from the value being shifted, not the count.
            if (op.Value is IRBinaryOperator.ShiftLeft or IRBinaryOperator.ShiftRight)
            {
                return OperandType(nested.LeftOperand);
            }

            return ChooseOperandType(StripWidening(nested.LeftOperand), StripWidening(nested.RightOperand));
        }

        if (operation.ConstantValue is { HasValue: true, Value: { } constant })
        {
            IRPrimitiveType? declaredConstantType = IntegerWidth.AsPrimitive(types.Map(operation.Type, null));

            // Prefer the declared type when the constant fits it, so an
            // explicitly ushort constant does not silently shrink to a byte.
            if (declaredConstantType is not null && IntegerWidth.Fits(constant, declaredConstantType))
            {
                return IntegerWidth.ForConstant(constant) is { } narrowest &&
                       narrowest.WidthInBits < declaredConstantType.WidthInBits &&
                       operation is ILiteralOperation
                    ? narrowest
                    : declaredConstantType;
            }

            return IntegerWidth.ForConstant(constant);
        }

        return IntegerWidth.AsPrimitive(types.Map(operation.Type, null));
    }

    /// <summary>
    /// Removes implicit widening conversions so the operand's real width is
    /// visible. These are the conversions C# inserted, not ones the developer
    /// wrote.
    /// </summary>
    private static IOperation StripWidening(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;

                case IConversionOperation conversion
                    when conversion.Conversion is { IsNumeric: true, IsImplicit: true } &&
                         IntegerWidth.IsWidening(conversion.Operand.Type, conversion.Type):
                    operation = conversion.Operand;
                    continue;

                default:
                    return operation;
            }
        }
    }

    private IRExpression MakeConstant(object value, IOperation operation, IRType? context)
    {
        IRType type = context as IRPrimitiveType is { } contextType && IntegerWidth.Fits(value, contextType)
            ? contextType
            : types.Map(operation.Type, null) ?? IntegerWidth.ForConstant(value) ?? IRPrimitiveType.U8;

        return new IRConstant(type, IntegerWidth.Normalize(value, type));
    }

    /// <summary>
    /// The zero value of a type. Scalars take a literal; anything with a layout
    /// needs the backend's shared zero instance.
    /// </summary>
    private static IRExpression DefaultOf(IRType type) => type is IRPrimitiveType
        ? new IRConstant(type, 0)
        : new IRDefaultValue(type);

    /// <summary>Inserts a conversion only when the widths actually differ.</summary>
    private static IRExpression Coerce(IRExpression expression, IRType target)
    {
        if (expression.Type == target || expression is IRUnit)
        {
            return expression;
        }

        // A constant can simply be reinterpreted; no conversion needs emitting.
        if (expression is IRConstant constant && target is IRPrimitiveType primitive &&
            IntegerWidth.Fits(constant.Value, primitive))
        {
            return new IRConstant(target, IntegerWidth.Normalize(constant.Value, target));
        }

        // An array already decays to a pointer to its first element in C, so no
        // cast is needed and none should be emitted. Array types compare unequal
        // whenever their lengths differ, and a declared 'byte[4]' meeting a
        // 'byte[]' parameter differs every time. The cast would also strip the
        // const off ROM data on its way to a native call.
        if (expression.Type is IRArrayType array && ElementTypeOf(target) == array.ElementType)
        {
            return expression;
        }

        return new IRConvert(expression, target);
    }

    /// <summary>The pointee or element type of a pointer or array, else null.</summary>
    private static IRType? ElementTypeOf(IRType type) => type switch
    {
        IRArrayType array => array.ElementType,
        IRPointerType pointer => pointer.PointeeType,
        _ => null,
    };

    private IRExpression? Contextualize(IRExpression expression, IRType? context) =>
        context is null ? expression : Coerce(expression, context);

    /// <summary>Makes an expression usable where C expects a truth value.</summary>
    private static IRExpression AsCondition(IRExpression expression) =>
        expression.Type == IRPrimitiveType.Bool
            ? expression
            : new IRBinary(
                IRBinaryOperator.NotEqual,
                expression,
                new IRConstant(expression.Type, 0),
                IRPrimitiveType.Bool);

    private static IRBlock AsBlock(IRStatement statement) =>
        statement as IRBlock ?? new IRBlock([statement]);

    private static IRStatement Flatten(IRStatement statement) =>
        statement is IRBlock { Statements.Count: 1 } block ? block.Statements[0] : statement;

    private static int? TryGetArrayLength(IOperation? initializer) => initializer switch
    {
        IArrayCreationOperation { DimensionSizes.Length: 1 } creation
            when creation.DimensionSizes[0].ConstantValue is { HasValue: true, Value: { } size } =>
            Convert.ToInt32(size, CultureInfo.InvariantCulture),

        IArrayCreationOperation { Initializer.ElementValues.Length: var count } => count,

        _ => null,
    };

    private void ReportWideArithmetic(ITypeSymbol? type, Location location)
    {
        if (type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32)
        {
            diagnostics.Report(GBDiagnostics.Int32Arithmetic, location, type.ToDisplayString());
        }
    }

    private static bool IsErasedHandle(ITypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == "GB";


    private IRStatement? Unsupported(IOperation operation, string what)
    {
        diagnostics.Report(GBDiagnostics.UnsupportedConstruct, Loc(operation), what);
        return null;
    }

    private IRStatement? Reject(GBDiagnosticDescriptor descriptor, IOperation operation, params object?[] args)
    {
        diagnostics.Report(descriptor, Loc(operation), args);
        return null;
    }

    private IRExpression? RejectExpression(GBDiagnosticDescriptor descriptor, IOperation operation, params object?[] args)
    {
        diagnostics.Report(descriptor, Loc(operation), args);
        return null;
    }

    private IRExpression? Fail(IOperation operation, string message)
    {
        diagnostics.Report(GBDiagnostics.InternalError, Loc(operation), message);
        return null;
    }

    private static Location Loc(IOperation operation) => operation.Syntax.GetLocation();

    /// <summary>
    /// Names the offending construct using its syntax kind, so GBS0001 says
    /// "'ForEach' is not supported" rather than naming an internal node type.
    /// </summary>
    private static string DescribeSyntax(IOperation operation)
    {
        string kind = operation.Syntax.RawKind is var _ && operation.Syntax is CSharpSyntaxNode node
            ? node.Kind().ToString()
            : operation.Kind.ToString();

        return $"'{kind.Replace("Expression", string.Empty).Replace("Statement", string.Empty)}'";
    }

    private static string DescribeOperator(IRBinaryOperator op) => op switch
    {
        IRBinaryOperator.Add => "addition",
        IRBinaryOperator.Subtract => "subtraction",
        IRBinaryOperator.Multiply => "multiplication",
        IRBinaryOperator.Divide => "division",
        IRBinaryOperator.Remainder => "remainder",
        IRBinaryOperator.ShiftLeft => "left shift",
        IRBinaryOperator.ShiftRight => "right shift",
        _ => "bitwise",
    };

    private static IRBinaryOperator? MapBinaryOperator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => IRBinaryOperator.Add,
        BinaryOperatorKind.Subtract => IRBinaryOperator.Subtract,
        BinaryOperatorKind.Multiply => IRBinaryOperator.Multiply,
        BinaryOperatorKind.Divide => IRBinaryOperator.Divide,
        BinaryOperatorKind.Remainder => IRBinaryOperator.Remainder,
        BinaryOperatorKind.And => IRBinaryOperator.BitwiseAnd,
        BinaryOperatorKind.Or => IRBinaryOperator.BitwiseOr,
        BinaryOperatorKind.ExclusiveOr => IRBinaryOperator.BitwiseXor,
        BinaryOperatorKind.LeftShift => IRBinaryOperator.ShiftLeft,
        BinaryOperatorKind.RightShift => IRBinaryOperator.ShiftRight,
        BinaryOperatorKind.Equals => IRBinaryOperator.Equal,
        BinaryOperatorKind.NotEquals => IRBinaryOperator.NotEqual,
        BinaryOperatorKind.LessThan => IRBinaryOperator.LessThan,
        BinaryOperatorKind.LessThanOrEqual => IRBinaryOperator.LessThanOrEqual,
        BinaryOperatorKind.GreaterThan => IRBinaryOperator.GreaterThan,
        BinaryOperatorKind.GreaterThanOrEqual => IRBinaryOperator.GreaterThanOrEqual,
        BinaryOperatorKind.ConditionalAnd => IRBinaryOperator.LogicalAnd,
        BinaryOperatorKind.ConditionalOr => IRBinaryOperator.LogicalOr,
        _ => null,
    };
}
