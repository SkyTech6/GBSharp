using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Frontend;

/// <summary>
/// Reads the <c>[assembly: MaxWRAM(...)]</c> family.
/// </summary>
/// <remarks>
/// Separate from <see cref="FrameworkSymbols"/> deliberately. That class's list is
/// the contract governing how a member <em>lowers</em>, and its own remarks say a
/// new entry there is an admission that nothing existing could express something.
/// A budget is not part of lowering: it is a fact about the finished ROM,
/// checked after the linker has run. So adding it there would quietly falsify
/// that claim.
/// </remarks>
public static class BudgetSymbols
{
    public static Budgets Resolve(Compilation compilation)
    {
        INamedTypeSymbol? wram = compilation.GetTypeByMetadataName("GB.MaxWRAMAttribute");
        INamedTypeSymbol? rom = compilation.GetTypeByMetadataName("GB.MaxROMAttribute");
        INamedTypeSymbol? banks = compilation.GetTypeByMetadataName("GB.MaxROMBanksAttribute");

        int? maxWram = null;
        int? maxRom = null;
        int? maxBanks = null;

        foreach (AttributeData attribute in compilation.Assembly.GetAttributes())
        {
            if (Value(attribute, wram) is { } w)
            {
                maxWram = w;
            }
            else if (Value(attribute, rom) is { } r)
            {
                maxRom = r;
            }
            else if (Value(attribute, banks) is { } b)
            {
                maxBanks = b;
            }
        }

        return new Budgets(maxWram, maxRom, maxBanks);
    }

    private static int? Value(AttributeData attribute, INamedTypeSymbol? type)
    {
        if (type is null || !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, type))
        {
            return null;
        }

        return attribute.ConstructorArguments is [{ Value: int value }] ? value : null;
    }
}
