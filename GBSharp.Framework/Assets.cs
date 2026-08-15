using System;

namespace GB;

/// <summary>
/// Converts an image into tiles at build time and places the result in ROM.
/// </summary>
/// <remarks>
/// <para>
/// The field itself holds nothing. The image is decoded, validated, converted
/// to 2bpp, deduplicated and turned into ROM data while the project is
/// building, and the field becomes a name for that data. Anything wrong with
/// the image is a compile error pointing at this declaration.
/// </para>
/// <para>
/// Paths resolve relative to the file that declares them, then to the project's
/// <c>Assets</c> folder, then to the project root.
/// </para>
/// <example>
/// <code>
/// [Asset("forest.png")]
/// private static TileMap Forest;
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class AssetAttribute : Attribute
{
    public AssetAttribute(string path) => Path = path;

    /// <summary>The image, relative to the declaring file or the Assets folder.</summary>
    public string Path { get; }

    /// <summary>A tighter tile budget than the hardware's, to fail early. 0 uses the hardware limit.</summary>
    public int MaxTiles { get; set; }

    /// <summary>
    /// Whether tiles that are mirrors of each other should share one copy.
    /// </summary>
    /// <remarks>
    /// Only possible where something can record the flip. On Game Boy Color the
    /// attribute map carries flip bits and this defaults to on; an original
    /// Game Boy's map has one byte per cell and no room for them, so it defaults
    /// to off and turning it on is an error rather than a silently wrong image.
    /// </remarks>
    public bool DedupeFlips { get; set; }
}

/// <summary>
/// Converts a sprite sheet into hardware sprite tiles at build time.
/// </summary>
/// <remarks>
/// Sheets are sliced into 8x8 tiles row-major, and flipped duplicates always
/// share one copy because OAM carries flip bits. That row-major order is the
/// order the hardware expects for 8x8 sprites; it is <em>not</em> what
/// <see cref="TallSprites"/> needs, because hardware 8x16 mode pairs each even
/// tile with the odd tile after it as (top, bottom) of one sprite, and a
/// row-major slice does not put a sprite's top and bottom tiles next to each
/// other. Set <see cref="TallSprites"/> to get the pairing 8x16 mode requires.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class SpriteAttribute : Attribute
{
    public SpriteAttribute(string path) => Path = path;

    /// <summary>The image, relative to the declaring file or the Assets folder.</summary>
    public string Path { get; }

    /// <summary>A tighter tile budget than the hardware's. 0 uses the hardware limit.</summary>
    public int MaxTiles { get; set; }

    /// <summary>
    /// Slices the sheet for hardware 8x16 sprites instead of 8x8.
    /// </summary>
    /// <remarks>
    /// Each sprite on the sheet must be a 16-pixel-tall column: the sheet's
    /// height must be a multiple of 16. The two tiles of a column are placed
    /// adjacent and even-aligned, top then bottom, which is what
    /// <see cref="Display.UseTallSprites"/> needs to draw them as one sprite.
    /// Deduplication runs on the pair as a unit so that invariant survives it.
    /// </remarks>
    public bool TallSprites { get; set; }
}

/// <summary>
/// Converts a sprite sheet into an animated hardware sprite, made of several
/// sub-sprites, at build time.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is a grid of frames, <see cref="FrameWidth"/> by
/// <see cref="FrameHeight"/> tiles each, read left-to-right then top-to-bottom.
/// A frame's own sub-sprites are whichever of its tiles are not entirely
/// transparent (palette index 0, the colour real hardware never draws for a
/// sprite), so a frame does not spend a hardware sprite, ROM, or a runtime OAM
/// write on empty space.
/// </para>
/// <para>
/// This is a different declaration from <see cref="SpriteAttribute"/>, not an
/// option on it: a metasprite's frames change the <em>shape</em> of the
/// converted data (a per-frame list of placements, not just a tile array), and
/// making that conditional on an attribute property would be exactly the
/// fragility a fixed argument list is meant to avoid.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class MetaspriteAttribute : Attribute
{
    public MetaspriteAttribute(string path) => Path = path;

    /// <summary>The image, relative to the declaring file or the Assets folder.</summary>
    public string Path { get; }

    /// <summary>One frame's width, in 8-pixel tiles. The sheet's width must be a multiple of this.</summary>
    public int FrameWidth { get; set; }

    /// <summary>One frame's height, in 8-pixel tiles. The sheet's height must be a multiple of this.</summary>
    public int FrameHeight { get; set; }

    /// <summary>A tighter tile budget than the hardware's. 0 uses the hardware limit.</summary>
    public int MaxTiles { get; set; }
}

/// <summary>
/// Converts a font sheet into background tiles and a character-to-tile lookup
/// at build time.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is one row of 8x8 glyphs, one tile per character in
/// <see cref="Characters"/>, left to right, so the image must be exactly
/// <c>Characters.Length</c> tiles wide and exactly one tile (8 pixels) tall.
/// That is a deliberate v1 simplification, not a missing feature: proportional
/// glyph widths need a per-glyph advance table and a text layout GB# does not
/// have yet, and monospaced text is what most Game Boy games draw anyway.
/// </para>
/// <para>
/// A font carries no colour of its own. A background cell already has whatever
/// palette or attribute is active there, so drawing text never touches either -
/// see <see cref="Text.Draw"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Font("font.png", Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,!?")]
/// private static FontAsset Alphabet;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class FontAttribute : Attribute
{
    public FontAttribute(string path) => Path = path;

    /// <summary>The image, relative to the declaring file or the Assets folder.</summary>
    public string Path { get; }

    /// <summary>
    /// The character set, in the order the glyphs appear on the sheet, left to
    /// right. Required: there is no default set of characters to fall back to.
    /// </summary>
    public string Characters { get; set; } = "";

    /// <summary>A tighter tile budget than the hardware's. 0 uses the hardware limit.</summary>
    public int MaxTiles { get; set; }
}

/// <summary>
/// Tiles and a map, from an image. Holds no state; the data is in ROM.
/// </summary>
public struct TileMap
{
}

/// <summary>Tiles only, for code that builds its own maps.</summary>
public struct TileSet
{
}

/// <summary>Sprite tiles, from a sheet.</summary>
public struct SpriteAsset
{
}

/// <summary>An animated hardware sprite's tiles and per-frame placements, from a sheet.</summary>
public struct MetaspriteAsset
{
}

/// <summary>A font's glyph tiles and character lookup, from a sheet. Holds no state.</summary>
public struct FontAsset
{
}

/// <summary>
/// Copies a file into ROM unchanged.
/// </summary>
/// <remarks>
/// <para>
/// For data GB# has no opinion about: level layouts, a table another tool
/// produced, anything already in the form your code wants. Nothing is converted
/// and nothing is validated beyond the file being there, which is exactly the
/// service being offered.
/// </para>
/// <para>
/// The alternative is a <c>static readonly byte[]</c> full of literals, which
/// works and is unreadable past about twenty bytes.
/// </para>
/// <example>
/// <code>
/// [Binary("level1.dat")]
/// private static BinaryAsset Level1;
///
/// byte first = Data.Read(Level1, 0);
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class BinaryAttribute : Attribute
{
    public BinaryAttribute(string path) => Path = path;

    /// <summary>The file, relative to the declaring file or the Assets folder.</summary>
    public string Path { get; }
}

/// <summary>Bytes in ROM, from a file. Holds no state.</summary>
public struct BinaryAsset
{
}

/// <summary>Reading a <see cref="BinaryAsset"/>.</summary>
public static class Data
{
    /// <summary>How many bytes the file held.</summary>
    [Native("gbs_data_length")]
    public static ushort Length(BinaryAsset asset) => throw FrameworkOnly.Declaration();

    /// <summary>One byte, by index. Not bounds-checked: the cartridge is not readable past its end.</summary>
    [Native("gbs_data_read")]
    public static byte Read(BinaryAsset asset, ushort index) => throw FrameworkOnly.Declaration();
}
