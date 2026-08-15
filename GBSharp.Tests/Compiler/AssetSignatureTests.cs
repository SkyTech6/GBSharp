using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Lowering;

namespace GBSharp.Tests.Compiler;

/// <summary>
/// Pins the argument shape each <see cref="AssetKind"/> expands into.
/// </summary>
/// <remarks>
/// This is a contract with <c>gbs_runtime.h</c> - see the remarks on
/// <see cref="AssetSignature"/> and on <c>AssetBinding.Arguments</c> - and a
/// test that only exercised one kind at a time would not notice a fourth shape
/// silently sharing an arm meant for a different one. Asserting count and
/// order per kind here is what makes that drift show up immediately.
/// </remarks>
public sealed class AssetSignatureTests
{
    [Fact]
    public void TileMapIsNineArgumentsEndingInBank()
    {
        Assert.Equal(
            [
                AssetSignatureArg.Tiles,
                AssetSignatureArg.Map,
                AssetSignatureArg.Attributes,
                AssetSignatureArg.Palettes,
                AssetSignatureArg.TileCount,
                AssetSignatureArg.Width,
                AssetSignatureArg.Height,
                AssetSignatureArg.PaletteCount,
                AssetSignatureArg.Bank,
            ],
            AssetSignature.For(AssetKind.TileMap));
    }

    /// <summary>
    /// A tileset has no map or attribute blobs, but it is still the same shim
    /// call with those pointers null - not a different signature.
    /// </summary>
    [Fact]
    public void TileSetSharesTheTileMapShape()
    {
        Assert.Equal(AssetSignature.For(AssetKind.TileMap), AssetSignature.For(AssetKind.TileSet));
    }

    [Fact]
    public void SpriteSheetIsFiveArgumentsEndingInBank()
    {
        Assert.Equal(
            [
                AssetSignatureArg.Tiles,
                AssetSignatureArg.Palettes,
                AssetSignatureArg.TileCount,
                AssetSignatureArg.PaletteCount,
                AssetSignatureArg.Bank,
            ],
            AssetSignature.For(AssetKind.SpriteSheet));
    }

    [Fact]
    public void FontIsFourArgumentsEndingInBank()
    {
        Assert.Equal(
            [
                AssetSignatureArg.Tiles,
                AssetSignatureArg.GlyphTable,
                AssetSignatureArg.TileCount,
                AssetSignatureArg.Bank,
            ],
            AssetSignature.For(AssetKind.Font));
    }

    [Theory]
    [InlineData(AssetKind.TileMap)]
    [InlineData(AssetKind.TileSet)]
    [InlineData(AssetKind.SpriteSheet)]
    [InlineData(AssetKind.Metasprite)]
    [InlineData(AssetKind.Font)]
    public void BankIsAlwaysLast(AssetKind kind)
    {
        Assert.Equal(AssetSignatureArg.Bank, AssetSignature.For(kind)[^1]);
    }

    [Fact]
    public void BinaryHasNoBlobShape()
    {
        // Binary never goes through AssetArtifact's blob roles at all - see
        // AssetBindings.MaterializeBinary - so there is nothing to declare here.
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetSignature.For(AssetKind.Binary));
    }
}
