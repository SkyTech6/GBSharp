using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// Turns a <c>[Bank]</c> attribute into the bank a declaration lands in.
/// </summary>
/// <remarks>
/// Separate from <see cref="FrameworkSymbols"/> on purpose. That class answers
/// "what did the developer write", which an analyzer running inside an IDE has
/// to be able to ask; this one answers "where does it go", which is a statement
/// about the IR and belongs to the compiler. Keeping the two apart is what lets
/// the recognition half be shared without dragging the IR into the IDE.
/// </remarks>
internal static class BankResolver
{
    /// <summary>
    /// The bank a declaration asks for, or null if it does not carry <c>[Bank]</c>.
    /// </summary>
    /// <remarks>
    /// A parameterless <c>[Bank]</c> means automatic. <c>[Bank(0)]</c> is not
    /// the same as no attribute: it is an explicit request to stay resident,
    /// which is what makes one method of a banked class stay mapped.
    /// </remarks>
    public static IRBank? Read(FrameworkSymbols framework, ISymbol symbol)
    {
        AttributeData? attribute = framework.GetBankAttribute(symbol);
        if (attribute is null)
        {
            return null;
        }

        if (attribute.ConstructorArguments.Length == 0)
        {
            return IRBank.Automatic;
        }

        if (attribute.ConstructorArguments[0].Value is int bank)
        {
            return bank == 0 ? IRBank.Resident : IRBank.Fixed(bank);
        }

        return null;
    }

    /// <summary>The bank a member lands in, inheriting from its containing type.</summary>
    /// <remarks>
    /// A member's own attribute wins, so a banked class can keep one method
    /// resident.
    /// </remarks>
    public static IRBank Resolve(FrameworkSymbols framework, ISymbol symbol) =>
        Read(framework, symbol)
        ?? (symbol.ContainingType is { } containing ? Read(framework, containing) : null)
        ?? IRBank.Resident;
}
