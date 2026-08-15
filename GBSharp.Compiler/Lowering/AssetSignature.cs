using GBSharp.Compiler.Assets;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// One argument in a converted asset's expansion, naming what it is rather
/// than where it comes from.
/// </summary>
/// <remarks>
/// See <see cref="AssetSignature"/> for why this exists.
/// </remarks>
public enum AssetSignatureArg
{
    /// <summary>Pointer to the tile data blob, <c>const uint8_t *</c>.</summary>
    Tiles,

    /// <summary>Pointer to the map indices blob, <c>const uint8_t *</c>. TileMap only.</summary>
    Map,

    /// <summary>Pointer to the Game Boy Color attribute map, <c>const uint8_t *</c>. TileMap only.</summary>
    Attributes,

    /// <summary>Pointer to the Game Boy Color palette table, <c>const uint16_t *</c>.</summary>
    Palettes,

    /// <summary>Unique tile count after deduplication, <c>uint8_t</c>.</summary>
    TileCount,

    /// <summary>Map width in tiles, <c>uint8_t</c>. TileMap only.</summary>
    Width,

    /// <summary>Map height in tiles, <c>uint8_t</c>. TileMap only.</summary>
    Height,

    /// <summary>Palette count, <c>uint8_t</c>.</summary>
    PaletteCount,

    /// <summary>
    /// Pointer to the packed <c>metasprite_t</c> records, <c>const uint8_t *</c>.
    /// Metasprite only.
    /// </summary>
    Frames,

    /// <summary>Pointer to the per-frame offset table, <c>const uint8_t *</c>. Metasprite only.</summary>
    FrameOffsets,

    /// <summary>Frame count, <c>uint8_t</c>. Metasprite only.</summary>
    FrameCount,

    /// <summary>
    /// Pointer to the 256-entry ASCII-to-tile lookup, <c>const uint8_t *</c>.
    /// Font only.
    /// </summary>
    GlyphTable,

    /// <summary>The ROM bank the data lives in, <c>uint8_t</c>. Always last.</summary>
    Bank,
}

/// <summary>
/// The C argument list each <see cref="AssetKind"/> expands into, as data
/// rather than as an if/else building an expression list by hand.
/// </summary>
/// <remarks>
/// <para>
/// This is the table <see cref="AssetBindings"/>'s own remarks ask for. At
/// three blob-shaped kinds (TileMap, TileSet, SpriteSheet) a hand-written
/// branch was still readable; Metasprite is the fourth, and a fourth arm would
/// have been one nobody could tell apart from the other two at a glance. A
/// table that says what each kind's arguments <em>are</em>, with one test
/// asserting every kind's shape, is the version that cannot silently drift
/// from <c>gbs_runtime.h</c>.
/// </para>
/// <para>
/// Metasprite's shape is a superset shared by two different native calls -
/// loading the sheet's tiles once, and moving a frame every time it is drawn -
/// exactly the way TileMap's shape is already shared by <c>Background.Load</c>
/// and <c>Background.DrawRegion</c>. Each shim ignores the arguments it has no
/// use for; see <c>gbs_runtime.c</c>.
/// </para>
/// <para>
/// <see cref="AssetKind.Binary"/> is not here. It never goes through
/// <see cref="AssetArtifact"/> or a blob role at all - see
/// <see cref="AssetBindings.MaterializeBinary"/> - so there is no blob table to
/// declare for it.
/// </para>
/// </remarks>
public static class AssetSignature
{
    private static readonly AssetSignatureArg[] TileMapShape =
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
    ];

    private static readonly AssetSignatureArg[] SpriteSheetShape =
    [
        AssetSignatureArg.Tiles,
        AssetSignatureArg.Palettes,
        AssetSignatureArg.TileCount,
        AssetSignatureArg.PaletteCount,
        AssetSignatureArg.Bank,
    ];

    private static readonly AssetSignatureArg[] MetaspriteShape =
    [
        AssetSignatureArg.Tiles,
        AssetSignatureArg.Palettes,
        AssetSignatureArg.Frames,
        AssetSignatureArg.FrameOffsets,
        AssetSignatureArg.TileCount,
        AssetSignatureArg.PaletteCount,
        AssetSignatureArg.FrameCount,
        AssetSignatureArg.Bank,
    ];

    private static readonly AssetSignatureArg[] FontShape =
    [
        AssetSignatureArg.Tiles,
        AssetSignatureArg.GlyphTable,
        AssetSignatureArg.TileCount,
        AssetSignatureArg.Bank,
    ];

    /// <summary>
    /// The argument list a converted asset of this kind expands into, in the
    /// order <c>gbs_runtime.h</c> declares its parameters.
    /// </summary>
    public static IReadOnlyList<AssetSignatureArg> For(AssetKind kind) => kind switch
    {
        // A tileset is a tile map with no map or attributes to emit - Assemble
        // never produces those blobs for it - but the argument shape is the
        // same shim call with null pointers in their place, not a different one.
        AssetKind.TileMap or AssetKind.TileSet => TileMapShape,
        AssetKind.SpriteSheet => SpriteSheetShape,
        AssetKind.Metasprite => MetaspriteShape,
        AssetKind.Font => FontShape,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "has no blob-based argument shape"),
    };
}
