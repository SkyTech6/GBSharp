using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Compiler.Assets;

/// <summary>What a declared asset is meant to become.</summary>
public enum AssetKind
{
    /// <summary>Tiles plus a map: a screen of background artwork.</summary>
    TileMap,

    /// <summary>Tiles only, for code that builds its own maps.</summary>
    TileSet,

    /// <summary>A sheet sliced into hardware sprite tiles.</summary>
    SpriteSheet,

    /// <summary>A sheet sliced into an animated sprite's per-frame sub-sprite placements.</summary>
    Metasprite,

    /// <summary>
    /// A one-row sheet of 8x8 glyphs, sliced into tiles plus an ASCII-to-tile
    /// lookup.
    /// </summary>
    /// <remarks>
    /// Carries no colour of its own: a background cell already has whatever
    /// palette or attribute is active there. See <c>GB.FontAttribute</c>.
    /// </remarks>
    Font,

    /// <summary>
    /// A file copied into ROM byte for byte.
    /// </summary>
    /// <remarks>
    /// Nothing is converted or validated beyond the file existing, because GB#
    /// has no idea what is in it. That is the point: level data, a compressed
    /// blob, a table someone else's tool produced.
    /// </remarks>
    Binary,
}

/// <summary>Which part of a converted asset a block of bytes is.</summary>
public enum AssetBlobRole
{
    /// <summary>2bpp tile data, 16 bytes per tile.</summary>
    TileData,

    /// <summary>One byte per map cell, indexing the tile data.</summary>
    MapIndices,

    /// <summary>Game Boy Color attributes: palette, flips and priority per cell.</summary>
    AttributeMap,

    /// <summary>Game Boy Color colours, four 16-bit entries per palette.</summary>
    Palettes,

    /// <summary>
    /// Packed <c>metasprite_t</c> records, one per sub-sprite, frame after frame,
    /// each frame ended by GBDK's own <c>metasprite_end</c> terminator record.
    /// </summary>
    MetaspriteFrames,

    /// <summary>One entry per frame: the index into <see cref="MetaspriteFrames"/> where that frame starts.</summary>
    FrameOffsets,

    /// <summary>
    /// 256 bytes, one per ASCII code: which tile in the font's own tile data
    /// draws that character. Font only.
    /// </summary>
    GlyphTable,
}

/// <summary>The machine an asset is being converted for.</summary>
public enum AssetTargetProfile
{
    GameBoy,
    GameBoyColor,
}

/// <param name="MaxTiles">A tighter budget than the hardware's, or 0 for the hardware limit.</param>
/// <param name="DedupeFlips">
/// Null means "decide from the target". Flipped tiles can only be deduplicated
/// where something can record the flip, which on a background means the Game Boy
/// Color attribute map.
/// </param>
/// <param name="TallSprites">
/// Sprite sheets only: slice into 8x16 column pairs instead of 8x8 tiles.
/// </param>
/// <param name="FrameWidth">Metasprites only: one frame's width, in tiles.</param>
/// <param name="FrameHeight">Metasprites only: one frame's height, in tiles.</param>
/// <param name="Characters">
/// Fonts only: the character set, in the order the glyphs appear on the sheet.
/// </param>
public sealed record AssetOptions(
    AssetTargetProfile Profile,
    int MaxTiles = 0,
    bool? DedupeFlips = null,
    bool TallSprites = false,
    int FrameWidth = 0,
    int FrameHeight = 0,
    string? Characters = null);

/// <param name="DisplayName">The C# field this came from, for diagnostics.</param>
/// <param name="Span">Where that field is declared. Every asset diagnostic points here.</param>
public sealed record AssetRequest(
    AssetKind Kind,
    string ResolvedPath,
    string DisplayName,
    AssetOptions Options,
    SourceSpan Span);

/// <param name="ElementWidth">Bytes per element: 1 for tiles and maps, 2 for colours.</param>
public sealed record AssetBlob(
    AssetBlobRole Role,
    string NameSuffix,
    ReadOnlyMemory<byte> Bytes,
    int ElementWidth = 1)
{
    public int ElementCount => Bytes.Length / ElementWidth;
}

/// <summary>What the conversion cost, for the build report.</summary>
public sealed record AssetStats(
    int WidthPixels,
    int HeightPixels,
    int WidthTiles,
    int HeightTiles,
    int TotalTiles,
    int UniqueTiles,
    int TilesSavedByFlip,
    int PaletteCount,
    int FrameCount = 0)
{
    public int TilesSavedByDeduplication => TotalTiles - UniqueTiles;
}

public sealed record AssetArtifact(IReadOnlyList<AssetBlob> Blobs, AssetStats Stats)
{
    public int RomBytes => Blobs.Sum(b => b.Bytes.Length);

    public AssetBlob? this[AssetBlobRole role] => Blobs.FirstOrDefault(b => b.Role == role);
}

/// <summary>
/// Turns an image on disk into bytes the target can use.
/// </summary>
/// <remarks>
/// <para>
/// The compiler drives conversion, because only Roslyn can read
/// <c>[Asset("forest.png")]</c> off a field, but it must not depend on an
/// image pipeline, so the implementation is passed in. This mirrors
/// <see cref="Frontend.CompilationRequest.FrameworkAssemblyPath"/>, which is a
/// path rather than a project reference for the same reason.
/// </para>
/// <para>
/// Everything crossing this boundary is bytes and counts. 2bpp tiles, 32x32
/// maps and BGR555 colours are properties of the Game Boy, not of GBDK, so
/// nothing here knows that C is the output format.
/// </para>
/// </remarks>
public interface IAssetCompiler
{
    /// <summary>
    /// Converts one asset, or returns null having reported why.
    /// </summary>
    AssetArtifact? Compile(AssetRequest request, DiagnosticBag diagnostics);
}

/// <summary>
/// The compiler used when no asset pipeline was supplied.
/// </summary>
/// <remarks>
/// Reports GBS0610 rather than throwing, so a host without an asset pipeline
/// produces a comprehensible error instead of a crash.
/// </remarks>
public sealed class NullAssetCompiler : IAssetCompiler
{
    public static NullAssetCompiler Instance { get; } = new();

    private NullAssetCompiler()
    {
    }

    public AssetArtifact? Compile(AssetRequest request, DiagnosticBag diagnostics)
    {
        diagnostics.Report(GBDiagnostics.AssetPipelineUnavailable, request.Span, request.DisplayName);
        return null;
    }
}
