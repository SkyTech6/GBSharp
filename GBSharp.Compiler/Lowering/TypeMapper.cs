using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;
using GBSharp.Rules;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// Maps C# types onto IR types, and rejects the ones GB# cannot represent.
/// </summary>
/// <remarks>
/// This is where the language subset of thesis section 6 is actually enforced
/// for types. Every rejection produces a diagnostic that names an alternative,
/// because "unsupported" on its own does not help anyone.
/// </remarks>
public sealed class TypeMapper(DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, IRStructType> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IRStruct> _structDeclarations = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IRStruct> Structs => _structDeclarations.Values;

    /// <summary>
    /// Set by <see cref="ModuleLowerer"/>. Fixed collections are resolved per
    /// declaration rather than per type, because two fields of the same
    /// <c>FixedList&lt;T&gt;</c> with different capacities are different C types.
    /// </summary>
    internal FixedCollections? Collections { get; set; }

    /// <summary>
    /// Maps the type of a declaration, resolving a fixed collection's capacity
    /// from the declaring symbol's <c>[Capacity]</c> attribute.
    /// </summary>
    internal IRType? MapDeclaration(ITypeSymbol? type, ISymbol declaringSymbol, Location? location)
    {
        if (Collections is not null && FixedCollections.IsFixedCollection(type) && type is not null)
        {
            return Collections.Specialize(type, declaringSymbol, this, location);
        }

        return Map(type, location);
    }

    /// <summary>
    /// Registers a user struct so its fields and size are known before any
    /// method body referring to it is lowered.
    /// </summary>
    public void DeclareStruct(INamedTypeSymbol symbol, Location? location)
    {
        string name = NameMangler.ForType(symbol);
        if (_structDeclarations.ContainsKey(name))
        {
            return;
        }

        // Reserve the name first so a struct that (illegally) contains itself
        // terminates instead of recursing forever.
        _structs[name] = new IRStructType(name, 0);

        var fields = new List<IRField>();
        foreach (IFieldSymbol field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
            {
                continue;
            }

            IRType? fieldType = MapDeclaration(field.Type, field, field.Locations.FirstOrDefault() ?? location);
            if (fieldType is null)
            {
                continue;
            }

            fields.Add(new IRField(field.Name, fieldType));
        }

        var declaration = new IRStruct(name, fields);
        _structDeclarations[name] = declaration;
        _structs[name] = new IRStructType(name, declaration.SizeInBytes);
    }

    /// <summary>
    /// Maps a C# type, reporting a diagnostic and returning null if GB# has no
    /// representation for it.
    /// </summary>
    public IRType? Map(ITypeSymbol? type, Location? location)
    {
        if (type is null)
        {
            return null;
        }

        // Whether the type is allowed at all is decided once, in GBSharp.Rules,
        // so the analyzer rejects exactly what a build rejects. What an accepted
        // type becomes is this class's business and stays here.
        RuleVerdict verdict = SubsetRules.ClassifyType(type);
        if (verdict.IsRejection)
        {
            diagnostics.Report(verdict.Descriptor!, location, verdict.Arguments);
            return null;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Void:
                return IRPrimitiveType.Void;
            case SpecialType.System_Boolean:
                return IRPrimitiveType.Bool;
            case SpecialType.System_Byte:
                return IRPrimitiveType.U8;
            case SpecialType.System_SByte:
                return IRPrimitiveType.I8;
            case SpecialType.System_UInt16:
                return IRPrimitiveType.U16;
            case SpecialType.System_Int16:
                return IRPrimitiveType.I16;
            case SpecialType.System_Int32:
                return IRPrimitiveType.I32;
            case SpecialType.System_UInt32:
                return IRPrimitiveType.U32;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return Map(underlying, location);
        }

        if (type is IArrayTypeSymbol array)
        {
            // Length is attached by the declaration site, which knows it; the
            // type alone does not carry a size in C#.
            IRType? element = Map(array.ElementType, location);
            return element is null ? null : new IRArrayType(element, 0);
        }

        if (FixedCollections.IsFixedCollection(type))
        {
            // Reached only where no declaration is in view, so the capacity is
            // unknowable here.
            diagnostics.Report(GBDiagnostics.CapacityRequired, location, type.ToDisplayString());
            return null;
        }

        if (type.TypeKind == TypeKind.Struct && type is INamedTypeSymbol structSymbol)
        {
            string name = NameMangler.ForType(structSymbol);
            if (_structs.TryGetValue(name, out IRStructType? mapped))
            {
                return mapped;
            }

            // A framework handle type such as SpriteTable or SpriteRef. These
            // never reach the backend: every member on them is [Native] or
            // [NativeIdentity], so the value itself is erased during lowering.
            return new IRStructType(name, 0);
        }

        // Every rejection was decided above by SubsetRules, so reaching here
        // means the type is accepted but this class has no mapping for it.
        diagnostics.Report(
            GBDiagnostics.InternalError,
            location,
            $"no IR representation for the accepted type '{type.ToDisplayString()}'");
        return null;
    }
}
