using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Frontend;

/// <summary>
/// The framework types the compiler must recognise, resolved once per compilation.
/// </summary>
/// <remarks>
/// <para>
/// GB# recognises framework members by symbol identity rather than by name, so
/// the framework can grow without the compiler changing. This list is the entire
/// contract between them, and it is meant to stay short: a new framework member
/// should need no compiler change at all, and a new entry here is an admission
/// that something could not be expressed with the ones that already exist.
/// </para>
/// <para>
/// The attributes answer two questions. <c>Native</c> and <c>NativeIdentity</c>
/// describe how a member lowers. <c>Asset</c> and <c>Sprite</c> mark a field
/// whose contents come from a file on disk rather than from source. <c>Capacity</c>
/// is a third: how much storage a declaration reserves.
/// </para>
/// <para>
/// The asset marker structs are here for a different reason. They carry no
/// behaviour, but a field's type is what selects the conversion, and matching
/// that by name would bind a user's own type called <c>TileMap</c> in any
/// namespace. Resolving them once and comparing symbols is what makes the
/// framework's types actually the framework's.
/// </para>
/// </remarks>
public sealed class FrameworkSymbols
{
    private FrameworkSymbols(
        INamedTypeSymbol? nativeAttribute,
        INamedTypeSymbol? nativeIdentityAttribute,
        INamedTypeSymbol? assetAttribute,
        INamedTypeSymbol? spriteAttribute,
        INamedTypeSymbol? capacityAttribute,
        INamedTypeSymbol? bankAttribute,
        INamedTypeSymbol? tileMap,
        INamedTypeSymbol? tileSet,
        INamedTypeSymbol? spriteAsset,
        INamedTypeSymbol? binaryAttribute,
        INamedTypeSymbol? binaryAsset,
        INamedTypeSymbol? metaspriteAttribute,
        INamedTypeSymbol? metaspriteAsset,
        INamedTypeSymbol? fontAttribute,
        INamedTypeSymbol? fontAsset)
    {
        BinaryAttribute = binaryAttribute;
        BinaryAsset = binaryAsset;
        NativeAttribute = nativeAttribute;
        NativeIdentityAttribute = nativeIdentityAttribute;
        AssetAttribute = assetAttribute;
        SpriteAttribute = spriteAttribute;
        CapacityAttribute = capacityAttribute;
        BankAttribute = bankAttribute;
        TileMap = tileMap;
        TileSet = tileSet;
        SpriteAsset = spriteAsset;
        MetaspriteAttribute = metaspriteAttribute;
        MetaspriteAsset = metaspriteAsset;
        FontAttribute = fontAttribute;
        FontAsset = fontAsset;
    }

    public INamedTypeSymbol? NativeAttribute { get; }

    public INamedTypeSymbol? NativeIdentityAttribute { get; }

    public INamedTypeSymbol? AssetAttribute { get; }

    public INamedTypeSymbol? SpriteAttribute { get; }

    public INamedTypeSymbol? CapacityAttribute { get; }

    public INamedTypeSymbol? BankAttribute { get; }

    public INamedTypeSymbol? TileMap { get; }

    public INamedTypeSymbol? TileSet { get; }

    public INamedTypeSymbol? SpriteAsset { get; }

    public INamedTypeSymbol? BinaryAttribute { get; }

    public INamedTypeSymbol? BinaryAsset { get; }

    public INamedTypeSymbol? MetaspriteAttribute { get; }

    public INamedTypeSymbol? MetaspriteAsset { get; }

    public INamedTypeSymbol? FontAttribute { get; }

    public INamedTypeSymbol? FontAsset { get; }

    /// <summary>True if GBSharp.Framework was resolvable at all.</summary>
    public bool IsAvailable => NativeAttribute is not null;

    public static FrameworkSymbols Resolve(Compilation compilation) => new(
        compilation.GetTypeByMetadataName("GB.NativeAttribute"),
        compilation.GetTypeByMetadataName("GB.NativeIdentityAttribute"),
        compilation.GetTypeByMetadataName("GB.AssetAttribute"),
        compilation.GetTypeByMetadataName("GB.SpriteAttribute"),
        compilation.GetTypeByMetadataName("GB.CapacityAttribute"),
        compilation.GetTypeByMetadataName("GB.BankAttribute"),
        compilation.GetTypeByMetadataName("GB.TileMap"),
        compilation.GetTypeByMetadataName("GB.TileSet"),
        compilation.GetTypeByMetadataName("GB.SpriteAsset"),
        compilation.GetTypeByMetadataName("GB.BinaryAttribute"),
        compilation.GetTypeByMetadataName("GB.BinaryAsset"),
        compilation.GetTypeByMetadataName("GB.MetaspriteAttribute"),
        compilation.GetTypeByMetadataName("GB.MetaspriteAsset"),
        compilation.GetTypeByMetadataName("GB.FontAttribute"),
        compilation.GetTypeByMetadataName("GB.FontAsset"));

    /// <summary>
    /// The <c>[Bank]</c> attribute on a declaration, if it carries one.
    /// </summary>
    /// <remarks>
    /// Returns the attribute rather than a bank so this class stays free of the
    /// IR. Everything here has to be usable by a Roslyn analyzer, which runs in
    /// the IDE's process and cannot load the compiler's own assemblies;
    /// interpreting the attribute is the compiler's job. See
    /// <c>Lowering.BankResolver</c>.
    /// </remarks>
    public AttributeData? GetBankAttribute(ISymbol symbol)
    {
        if (BankAttribute is null)
        {
            return null;
        }

        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, BankAttribute))
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>
    /// The capacity a declaration reserves, from <c>[Capacity(n)]</c>.
    /// </summary>
    public int? GetCapacity(ISymbol symbol)
    {
        if (CapacityAttribute is null)
        {
            return null;
        }

        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, CapacityAttribute))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int capacity)
            {
                return capacity;
            }
        }

        return null;
    }

    /// <summary>True if the type is the framework's own asset marker struct.</summary>
    public bool IsTileMap(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, TileMap);

    public bool IsTileSet(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, TileSet);

    public bool IsSpriteAsset(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, SpriteAsset);

    public bool IsMetaspriteAsset(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, MetaspriteAsset);

    public bool IsFontAsset(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, FontAsset);

    /// <summary>
    /// The <c>[Asset]</c>, <c>[Sprite]</c>, <c>[Metasprite]</c>, <c>[Font]</c> or
    /// <c>[Binary]</c> attribute on a field, if it has one.
    /// </summary>
    public AttributeData? GetAssetAttribute(ISymbol symbol)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, AssetAttribute) ||
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, SpriteAttribute) ||
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, MetaspriteAttribute) ||
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, FontAttribute) ||
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, BinaryAttribute))
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>True if the attribute is <c>[Binary]</c> rather than an image one.</summary>
    public bool IsBinaryAttribute(AttributeData attribute) =>
        SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, BinaryAttribute);

    public bool IsBinaryAsset(ITypeSymbol? type) =>
        SymbolEqualityComparer.Default.Equals(type, BinaryAsset);

    /// <summary>True if the attribute is <c>[Sprite]</c> rather than <c>[Asset]</c>.</summary>
    public bool IsSpriteAttribute(AttributeData attribute) =>
        SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, SpriteAttribute);

    /// <summary>True if the attribute is <c>[Metasprite]</c> rather than <c>[Asset]</c>.</summary>
    public bool IsMetaspriteAttribute(AttributeData attribute) =>
        SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, MetaspriteAttribute);

    /// <summary>True if the attribute is <c>[Font]</c> rather than <c>[Asset]</c>.</summary>
    public bool IsFontAttribute(AttributeData attribute) =>
        SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, FontAttribute);

    /// <summary>
    /// The C symbol a member maps to, or null if it is ordinary GB# code.
    /// </summary>
    /// <remarks>
    /// Accessors are checked before the property they belong to, so a property
    /// can name a different symbol for reading and writing
    /// (<c>gbs_sprite_get_x</c> against <c>gbs_sprite_set_x</c>) while a
    /// read-only property can simply carry one attribute on itself.
    /// </remarks>
    public string? GetNativeSymbol(ISymbol symbol)
    {
        if (NativeAttribute is null)
        {
            return null;
        }

        string? direct = ReadSymbolArgument(symbol);
        if (direct is not null)
        {
            return direct;
        }

        // An accessor inherits the attribute from its property.
        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated })
        {
            return ReadSymbolArgument(associated);
        }

        return null;
    }

    /// <summary>True if the member lowers to its argument rather than to a call.</summary>
    public bool IsNativeIdentity(ISymbol symbol)
    {
        if (NativeIdentityAttribute is null)
        {
            return false;
        }

        if (HasAttribute(symbol, NativeIdentityAttribute))
        {
            return true;
        }

        return symbol is IMethodSymbol { AssociatedSymbol: { } associated }
            && HasAttribute(associated, NativeIdentityAttribute);
    }

    private string? ReadSymbolArgument(ISymbol symbol)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, NativeAttribute))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is string value &&
                value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType) =>
        symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
}
