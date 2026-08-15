using GBSharp.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;

namespace GBSharp.Rules;

/// <summary>
/// A rule's answer: accepted, or the diagnostic that rejects it.
/// </summary>
/// <remarks>
/// Carries the descriptor and its message arguments rather than a formatted
/// string, so the compiler and an analyzer produce identical text from one
/// definition.
/// </remarks>
public readonly record struct RuleVerdict(GBDiagnosticDescriptor? Descriptor, object?[] Arguments)
{
    public static RuleVerdict Accepted => default;

    public bool IsRejection => Descriptor is not null;

    public static RuleVerdict Reject(GBDiagnosticDescriptor descriptor, params object?[] arguments) =>
        new(descriptor, arguments);
}

/// <summary>
/// The language-subset rules, decided from symbols alone.
/// </summary>
/// <remarks>
/// <para>
/// This is the single definition of what GB# accepts. The compiler consults it
/// while lowering and the analyzers consult it while you type; both report the
/// same id with the same message, because there is only one copy of the answer.
/// </para>
/// <para>
/// Nothing here reads a file, walks a method body, or needs the width inference
/// the lowerer does. Rules that need any of those stay in the lowerer and are
/// reported at build time only: an analyzer that has to open a PNG to answer a
/// question is an analyzer that cannot run on every keystroke.
/// </para>
/// </remarks>
public static class SubsetRules
{
    /// <summary>
    /// Whether a type can exist on the target at all.
    /// </summary>
    /// <remarks>
    /// The order of the rejections is load-bearing. <c>string</c> is a class, so
    /// checking classes first would answer "allocation" for it instead of naming
    /// the type; the same is true of the BCL collections. Each check that comes
    /// early is there because a later one would give a worse answer.
    /// </remarks>
    public static RuleVerdict ClassifyType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return RuleVerdict.Accepted;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Void:
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int16:

            // Permitted but expensive. The cost is reported where the arithmetic
            // happens, not where the type is named, so a lone int constant that
            // folds away does not nag.
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                return RuleVerdict.Accepted;

            case SpecialType.System_Char:
            case SpecialType.System_String:
                return RuleVerdict.Reject(GBDiagnostics.StringType);

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return RuleVerdict.Reject(GBDiagnostics.UnsupportedType, type.ToDisplayString());
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return ClassifyType(underlying);
        }

        if (type is IArrayTypeSymbol array)
        {
            return ClassifyType(array.ElementType);
        }

        if (type.TypeKind == TypeKind.Struct)
        {
            return RuleVerdict.Accepted;
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            return RuleVerdict.Reject(GBDiagnostics.Interfaces, type.ToDisplayString());
        }

        if (type.TypeKind == TypeKind.Delegate)
        {
            return RuleVerdict.Reject(GBDiagnostics.DelegatesAndEvents, type.ToDisplayString());
        }

        if (IsDynamicCollection(type))
        {
            return RuleVerdict.Reject(GBDiagnostics.DynamicCollection, type.ToDisplayString());
        }

        if (type.TypeKind == TypeKind.Class)
        {
            return RuleVerdict.Reject(GBDiagnostics.ReferenceTypeAllocation, type.ToDisplayString());
        }

        // Type parameters, pointers, dynamic, and anything else the language
        // grows that GB# has not been taught.
        return RuleVerdict.Reject(GBDiagnostics.UnsupportedType, type.ToDisplayString());
    }

    /// <summary>
    /// True for the BCL collection types developers reach for first, so they get
    /// GBS0042 and a pointer at FixedList rather than a generic rejection.
    /// </summary>
    public static bool IsDynamicCollection(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named)
        {
            return false;
        }

        return named.ConstructedFrom
            .ToDisplayString()
            .StartsWith("System.Collections.Generic.", StringComparison.Ordinal);
    }

    /// <summary>True for a call to a LINQ operator, by the namespace declaring it.</summary>
    /// <remarks>
    /// Query syntax lowers to these same calls, so matching the method covers
    /// both spellings, and matching the namespace catches the operators nobody
    /// thought to enumerate.
    /// </remarks>
    public static bool IsLinq(IMethodSymbol method) =>
        method.ContainingType?.ContainingNamespace?.ToDisplayString() == "System.Linq";
}
