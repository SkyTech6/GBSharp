using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>One specialised fixed-capacity collection.</summary>
internal sealed record FixedCollectionInfo(
    IRStructType StructType,
    IRStruct Declaration,
    IRType ElementType,
    int Capacity,
    bool IsList)
{
    public const string ItemsField = "items";
    public const string CountField = "count";

    public IRArrayType StorageType => new(ElementType, Capacity);

    public string AddFunction => $"{StructType.Name}_Add";

    public string RemoveAtFunction => $"{StructType.Name}_RemoveAt";

    public string ClearFunction => $"{StructType.Name}_Clear";
}

/// <summary>
/// Specialises <c>FixedArray&lt;T&gt;</c> and <c>FixedList&lt;T&gt;</c> at
/// compile time.
/// </summary>
/// <remarks>
/// <para>
/// Each distinct element type and capacity becomes its own emitted C struct,
/// and the list operations become ordinary functions over that struct. Nothing
/// generic survives into the ROM: this is compile-time abstraction paid for at
/// compile time (thesis sections 3.4 and 11).
/// </para>
/// <para>
/// The helpers are built as IR rather than as C text, so they pass through the
/// same emitter as user code and appear in the generated file in the same
/// readable form.
/// </para>
/// </remarks>
internal sealed class FixedCollections(FrameworkSymbols framework, DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, FixedCollectionInfo> _specializations = new(StringComparer.Ordinal);
    private readonly List<IRFunction> _helpers = [];

    /// <summary>Structs to emit, one per specialisation.</summary>
    public IEnumerable<IRStruct> Structs => _specializations.Values.Select(s => s.Declaration);

    /// <summary>Generated list operations, appended to the module's functions.</summary>
    public IReadOnlyList<IRFunction> Helpers => _helpers;

    public static bool IsFixedCollection(ITypeSymbol? type) => Kind(type) is not null;

    private static bool? Kind(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named)
        {
            return null;
        }

        string name = named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty);

        return name switch
        {
            "GB.FixedArray<T>" => false,
            "GB.FixedList<T>" => true,
            _ => named.ConstructedFrom.MetadataName switch
            {
                "FixedArray`1" when named.ContainingNamespace?.Name == "GB" => false,
                "FixedList`1" when named.ContainingNamespace?.Name == "GB" => true,
                _ => null,
            },
        };
    }

    /// <summary>
    /// Resolves the specialised struct for a declaration, reading its capacity
    /// from <c>[Capacity(n)]</c>.
    /// </summary>
    public IRStructType? Specialize(
        ITypeSymbol type,
        ISymbol declaringSymbol,
        TypeMapper types,
        Location? location)
    {
        bool? isList = Kind(type);
        if (isList is null || type is not INamedTypeSymbol named)
        {
            return null;
        }

        int? capacity = framework.GetCapacity(declaringSymbol);
        if (capacity is null)
        {
            diagnostics.Report(GBDiagnostics.CapacityRequired, location, declaringSymbol.Name);
            return null;
        }

        if (capacity is < 1 or > 255)
        {
            diagnostics.Report(GBDiagnostics.CapacityInvalid, location, capacity.Value, declaringSymbol.Name);
            return null;
        }

        IRType? elementType = types.Map(named.TypeArguments[0], location);
        if (elementType is null)
        {
            return null;
        }

        return Intern(elementType, capacity.Value, isList.Value);
    }

    private IRStructType Intern(IRType elementType, int capacity, bool isList)
    {
        string prefix = isList ? "FixedList" : "FixedArray";
        string name = $"{prefix}_{SanitizeTypeName(elementType)}_{capacity}";

        if (_specializations.TryGetValue(name, out FixedCollectionInfo? existing))
        {
            return existing.StructType;
        }

        var fields = new List<IRField>
        {
            new(FixedCollectionInfo.ItemsField, new IRArrayType(elementType, capacity)),
        };

        if (isList)
        {
            fields.Add(new IRField(FixedCollectionInfo.CountField, IRPrimitiveType.U8));
        }

        var declaration = new IRStruct(name, fields);
        var structType = new IRStructType(name, declaration.SizeInBytes);
        var info = new FixedCollectionInfo(structType, declaration, elementType, capacity, isList);

        _specializations[name] = info;

        if (isList)
        {
            _helpers.AddRange(BuildListHelpers(info));
        }

        return structType;
    }

    public FixedCollectionInfo? Lookup(IRType? type) =>
        type is IRStructType structType && _specializations.TryGetValue(structType.Name, out FixedCollectionInfo? info)
            ? info
            : null;

    private static string SanitizeTypeName(IRType type) =>
        new(type.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // -----------------------------------------------------------------------
    // Generated operations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds Add, RemoveAt and Clear as IR functions over the specialised struct.
    /// </summary>
    private static IEnumerable<IRFunction> BuildListHelpers(FixedCollectionInfo info)
    {
        yield return BuildAdd(info);
        yield return BuildRemoveAt(info);
        yield return BuildClear(info);
    }

    private static IRFunction BuildAdd(FixedCollectionInfo info)
    {
        var self = new IRParameter("self", new IRPointerType(info.StructType));
        var item = new IRParameter("item", info.ElementType);

        IRExpression count = Count(info, self);
        IRExpression items = Items(info, self);

        var body = new IRBlock(
        [
            // A full list cannot grow, so the caller is told rather than surprised.
            new IRIf(
                new IRBinary(
                    IRBinaryOperator.GreaterThanOrEqual,
                    count,
                    new IRConstant(IRPrimitiveType.U8, (byte)info.Capacity),
                    IRPrimitiveType.Bool),
                new IRBlock([new IRReturn(IRConstant.Bool(false))]),
                null),

            new IRAssign(
                new IRElementAccess(items, count, info.ElementType),
                new IRParameterRef(item)),

            new IRExpressionStatement(new IRIncrement(count, IsDecrement: false, IRPrimitiveType.U8)),

            new IRReturn(IRConstant.Bool(true)),
        ]);

        return new IRFunction(
            info.AddFunction,
            IRPrimitiveType.Bool,
            [self, item],
            [],
            body,
            SourceSpan.None)
        {
            SourceName = $"FixedList<{info.ElementType.DisplayName}>.Add, capacity {info.Capacity}",
            IsCompilerGenerated = true,
        };
    }

    private static IRFunction BuildRemoveAt(FixedCollectionInfo info)
    {
        var self = new IRParameter("self", new IRPointerType(info.StructType));
        var index = new IRParameter("index", IRPrimitiveType.U8);

        IRExpression count = Count(info, self);
        IRExpression items = Items(info, self);

        var body = new IRBlock(
        [
            new IRIf(
                new IRBinary(
                    IRBinaryOperator.Equal,
                    count,
                    IRConstant.U8(0),
                    IRPrimitiveType.Bool),
                new IRBlock([new IRReturn(null)]),
                null),

            // Swap-remove: the last item fills the gap. Order is not preserved,
            // which is the cheap choice and the one worth defaulting to here.
            new IRExpressionStatement(new IRIncrement(count, IsDecrement: true, IRPrimitiveType.U8)),

            new IRAssign(
                new IRElementAccess(items, new IRParameterRef(index), info.ElementType),
                new IRElementAccess(items, count, info.ElementType)),
        ]);

        return new IRFunction(
            info.RemoveAtFunction,
            IRPrimitiveType.Void,
            [self, index],
            [],
            body,
            SourceSpan.None)
        {
            SourceName = $"FixedList<{info.ElementType.DisplayName}>.RemoveAt, capacity {info.Capacity}",
            IsCompilerGenerated = true,
        };
    }

    private static IRFunction BuildClear(FixedCollectionInfo info)
    {
        var self = new IRParameter("self", new IRPointerType(info.StructType));

        var body = new IRBlock(
        [
            new IRAssign(Count(info, self), IRConstant.U8(0)),
        ]);

        return new IRFunction(
            info.ClearFunction,
            IRPrimitiveType.Void,
            [self],
            [],
            body,
            SourceSpan.None)
        {
            SourceName = $"FixedList<{info.ElementType.DisplayName}>.Clear, capacity {info.Capacity}",
            IsCompilerGenerated = true,
        };
    }

    private static IRExpression Count(FixedCollectionInfo info, IRParameter self) =>
        new IRFieldAccess(Self(info, self), FixedCollectionInfo.CountField, IRPrimitiveType.U8);

    private static IRExpression Items(FixedCollectionInfo info, IRParameter self) =>
        new IRFieldAccess(Self(info, self), FixedCollectionInfo.ItemsField, info.StorageType);

    private static IRExpression Self(FixedCollectionInfo info, IRParameter self) =>
        new IRDereference(new IRParameterRef(self), info.StructType);
}
