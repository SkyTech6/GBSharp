using System.Text;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// Turns C# symbol names into C identifiers.
/// </summary>
/// <remarks>
/// The names are readable on purpose: <c>Game.Player.Update</c> becomes
/// <c>Game_Player_Update</c>, not a hash. A developer reading the generated C,
/// a linker map, or an emulator's symbol view has to be able to find their own
/// code (thesis section 3.3).
/// </remarks>
public static class NameMangler
{
    public static string ForMethod(IMethodSymbol method) =>
        Combine(QualifiedParts(method.ContainingType).Append(method.Name));

    public static string ForType(INamedTypeSymbol type) =>
        Combine(QualifiedParts(type));

    public static string ForGlobal(IFieldSymbol field) =>
        Combine(QualifiedParts(field.ContainingType).Append(field.Name));

    private static IEnumerable<string> QualifiedParts(INamedTypeSymbol? type)
    {
        var parts = new List<string>();

        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, current.Name);
        }

        var namespaceParts = new List<string>();
        for (INamespaceSymbol? ns = type?.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            namespaceParts.Insert(0, ns.Name);
        }

        return namespaceParts.Concat(parts);
    }

    private static string Combine(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();

        foreach (string part in parts)
        {
            if (sb.Length > 0)
            {
                sb.Append('_');
            }

            Sanitize(sb, part);
        }

        return sb.ToString();
    }

    private static void Sanitize(StringBuilder sb, string part)
    {
        foreach (char c in part)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
    }
}
