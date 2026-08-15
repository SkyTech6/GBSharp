using GBSharp.Assets.Images;
using GBSharp.Assets.Tiles;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Assets.Pipeline;

/// <summary>
/// PNG in, Game Boy tiles out.
/// </summary>
/// <remarks>
/// Every way this can fail becomes a GB# diagnostic reported at the C# field
/// that declared the asset, so an artist's problem with an image is shown
/// against the line of code that asked for it.
/// </remarks>
public sealed class PngAssetCompiler : IAssetCompiler
{
    /// <summary>
    /// The tile ceiling. <c>set_bkg_data</c> takes its count in a
    /// <c>uint8_t</c>, so 255 is the hardware's answer, not a chosen limit.
    /// </summary>
    public const int MaxTiles = 255;

    /// <summary>
    /// The largest map GB# will convert, per side, in tiles.
    /// </summary>
    /// <remarks>
    /// The hardware's own map is 32x32, but a map larger than that is still
    /// useful: <c>Background.DrawRegion</c> copies a window of it into the
    /// hardware map, which is how a world larger than one screen scrolls.
    /// <para>
    /// 128 rather than 255 because the bytes have to fit somewhere. A 128x128 map
    /// is 16 KB, exactly one ROM bank; 255x255 would be 65,025, larger than any
    /// bank can hold. Lifting this further needs the map split across banks,
    /// which is a different feature.
    /// </para>
    /// </remarks>
    public const int MaxMapTiles = 128;

    /// <summary>The hardware background map, in tiles per side.</summary>
    public const int HardwareMapTiles = 32;

    public AssetArtifact? Compile(AssetRequest request, DiagnosticBag diagnostics)
    {
        string name = Path.GetFileName(request.ResolvedPath);

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(request.ResolvedPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diagnostics.Report(GBDiagnostics.MalformedImage, request.Span, name, e.Message);
            return null;
        }

        if (!PngDecoder.TryDecode(bytes, out DecodedImage? image, out PngFailure? failure))
        {
            diagnostics.Report(
                failure!.IsUnsupportedFeature
                    ? GBDiagnostics.UnsupportedImageFeature
                    : GBDiagnostics.MalformedImage,
                request.Span,
                name,
                failure.Message);
            return null;
        }

        if (image!.Width % GbTile.Size != 0 || image.Height % GbTile.Size != 0)
        {
            diagnostics.Report(
                GBDiagnostics.DimensionsNotTileAligned,
                request.Span,
                name,
                image.Width,
                image.Height);
            return null;
        }

        if (request.Options.TallSprites && image.Height % 16 != 0)
        {
            diagnostics.Report(GBDiagnostics.TallSpriteHeightNotAligned, request.Span, name, image.Height);
            return null;
        }

        int widthTiles = image.Width / GbTile.Size;
        int heightTiles = image.Height / GbTile.Size;

        if (request.Kind == AssetKind.Metasprite)
        {
            int frameWidth = request.Options.FrameWidth;
            int frameHeight = request.Options.FrameHeight;

            if (frameWidth <= 0 || frameHeight <= 0 || widthTiles % frameWidth != 0 || heightTiles % frameHeight != 0)
            {
                diagnostics.Report(
                    GBDiagnostics.MetaspriteSheetNotDivisible,
                    request.Span,
                    name,
                    widthTiles,
                    heightTiles,
                    frameWidth,
                    frameHeight);
                return null;
            }
        }

        if (request.Kind == AssetKind.TileMap &&
            (widthTiles > MaxMapTiles || heightTiles > MaxMapTiles))
        {
            diagnostics.Report(
                GBDiagnostics.MapTooLarge, request.Span, name, widthTiles, heightTiles, MaxMapTiles);
            return null;
        }

        // Legal, and worth saying: Background.Load can only fill the hardware
        // map, so a larger one needs DrawRegion to be seen in full.
        if (request.Kind == AssetKind.TileMap &&
            (widthTiles > HardwareMapTiles || heightTiles > HardwareMapTiles))
        {
            diagnostics.Report(
                GBDiagnostics.MapLargerThanHardware, request.Span, name, widthTiles, heightTiles);
        }

        // A font sheet is one row: one glyph per declared character, left to
        // right, and nothing else on the sheet for GB# to guess about.
        if (request.Kind == AssetKind.Font)
        {
            int expectedWidth = request.Options.Characters?.Length ?? 0;

            if (widthTiles != expectedWidth || heightTiles != 1)
            {
                diagnostics.Report(
                    GBDiagnostics.FontSheetShapeMismatch,
                    request.Span,
                    name,
                    widthTiles,
                    heightTiles,
                    expectedWidth);
                return null;
            }
        }

        // A font never carries its own colour - a background cell already has
        // whatever palette or attribute is active there, and FontShape has no
        // room for one - so it converts as monochrome regardless of target
        // rather than silently building a palette nothing will ever read.
        bool color = request.Kind != AssetKind.Font && request.Options.Profile == AssetTargetProfile.GameBoyColor;

        IndexedImage? indexed = color
            ? PaletteBuilder.BuildColor(image, widthTiles, heightTiles, out PaletteFailure? paletteFailure)
            : PaletteBuilder.BuildMonochrome(image, widthTiles, heightTiles, out paletteFailure);

        if (indexed is null)
        {
            ReportPaletteFailure(paletteFailure!, request, diagnostics, name);
            return null;
        }

        // A font is never converted with colour (see above), so this would
        // otherwise misreport "the original Game Boy cannot show this" for a
        // font sheet built for Game Boy Color, where the real reason is that
        // Font does not manage colour at all, on either machine.
        if (request.Kind != AssetKind.Font && !color && UsesColor(image))
        {
            diagnostics.Report(GBDiagnostics.NonGreyscaleOnGameBoy, request.Span, name);
        }

        bool? requested = request.Options.DedupeFlips;

        // Sprites carry flip bits in OAM and colour maps carry them in the
        // attribute byte. A monochrome background map has one byte per cell and
        // nowhere to record a flip, so asking for it there is an error rather
        // than something to quietly ignore and draw wrongly.
        bool flipCapable = request.Kind == AssetKind.SpriteSheet || color;

        if (requested == true && !flipCapable)
        {
            diagnostics.Report(GBDiagnostics.FlipDeduplicationUnavailable, request.Span, request.DisplayName);
            return null;
        }

        bool dedupeFlips = requested ?? flipCapable;
        IReadOnlyList<GbTile> uniqueTiles;
        int savedByFlip;
        TilePlacement[]? placements = null;

        // Tall sprites dedupe at pair granularity: see TilePairDeduplicator's
        // remarks for why per-tile dedup is wrong here. Everything else -
        // TileMap, TileSet, and 8x8 SpriteSheet - keeps slicing tile by tile,
        // which is also what produces the map a TileMap needs.
        if (request.Kind == AssetKind.SpriteSheet && request.Options.TallSprites)
        {
            (uniqueTiles, savedByFlip) = SliceTallSpritePairs(indexed, image.Width, widthTiles, heightTiles, dedupeFlips);
        }
        else
        {
            var deduplicator = new TileDeduplicator(dedupeFlips);
            var slots = new TilePlacement[widthTiles * heightTiles];

            // Only a metasprite's sub-sprites are dropped for being blank: index 0
            // is a real, drawable colour on a background, and only OAM treats it
            // as transparent. Dropping the sub-sprite - not just its tile - is the
            // whole point of a metasprite: one fewer OAM entry, not just fewer bytes.
            bool dropBlankSubSprites = request.Kind == AssetKind.Metasprite;

            for (int ty = 0; ty < heightTiles; ty++)
            {
                for (int tx = 0; tx < widthTiles; tx++)
                {
                    byte[] cellIndices = TileIndices(indexed.PixelIndices, image.Width, tx, ty);

                    slots[(ty * widthTiles) + tx] = dropBlankSubSprites && Array.TrueForAll(cellIndices, index => index == 0)
                        ? new TilePlacement(-1, false, false)
                        : deduplicator.Add(GbTile.Encode(cellIndices));
                }
            }

            placements = slots;
            uniqueTiles = deduplicator.Unique;
            savedByFlip = deduplicator.SavedByFlip;
        }

        int budget = request.Options.MaxTiles > 0 ? Math.Min(request.Options.MaxTiles, MaxTiles) : MaxTiles;

        if (uniqueTiles.Count > budget)
        {
            diagnostics.Report(
                GBDiagnostics.TileBudgetExceeded,
                request.Span,
                name,
                uniqueTiles.Count,
                budget);
            return null;
        }

        byte[]? metaspriteFrames = null;
        byte[]? frameOffsets = null;
        int frameCount = 0;

        if (request.Kind == AssetKind.Metasprite)
        {
            (byte[] frameBytes, int[] offsets, int count) = BuildMetaspriteFrames(
                indexed, placements!, widthTiles, heightTiles, request.Options.FrameWidth, request.Options.FrameHeight);

            int totalEntries = frameBytes.Length / MetaspriteEntryBytes;

            if (totalEntries > 255)
            {
                diagnostics.Report(GBDiagnostics.MetaspriteTooManySubSprites, request.Span, name, totalEntries);
                return null;
            }

            metaspriteFrames = frameBytes;
            frameOffsets = Array.ConvertAll(offsets, o => (byte)o);
            frameCount = count;
        }

        byte[]? glyphTable = null;

        if (request.Kind == AssetKind.Font)
        {
            glyphTable = BuildGlyphTable(request.Options.Characters!, placements!);
        }

        return Assemble(
            request, indexed, uniqueTiles, savedByFlip, placements, image, widthTiles, heightTiles, color,
            metaspriteFrames, frameOffsets, frameCount, glyphTable);
    }

    /// <summary>
    /// Builds the 256-entry ASCII-to-tile lookup <c>Text.Draw</c> indexes
    /// directly: each declared character maps to its glyph's index in the
    /// deduplicated tileset, and every other code is left at 0 - drawing
    /// whichever glyph is tile 0 rather than being checked here, which is a
    /// build-time-only concern (see <c>GB.FontAttribute</c>'s remarks).
    /// </summary>
    private static byte[] BuildGlyphTable(string characters, TilePlacement[] placements)
    {
        var table = new byte[256];

        for (int i = 0; i < characters.Length; i++)
        {
            table[(byte)characters[i]] = (byte)placements[i].Index;
        }

        return table;
    }

    /// <summary>Bytes in one packed <c>metasprite_t</c> record: dy, dx, tile, props.</summary>
    private const int MetaspriteEntryBytes = 4;

    /// <summary>
    /// GBDK's own <c>metasprite_end</c> sentinel (<c>gb/metasprites.h</c>): a
    /// frame is terminated by a record whose <c>dy</c> is -128, which is not a
    /// reachable sub-sprite offset. <c>move_metasprite_ex</c> reads a frame's
    /// entries until it sees this, so GB# does not pass it a count - only the
    /// frame's starting address, from <see cref="AssetBlobRole.FrameOffsets"/>.
    /// </summary>
    private const int MetaspriteEnd = -128;

    /// <summary>
    /// Builds the flat, offset-indexed frame data <c>move_metasprite_ex</c>
    /// reads directly: each frame's non-blank sub-sprites, frame-local and
    /// row-major, followed by GBDK's terminator record.
    /// </summary>
    private static (byte[] Frames, int[] FrameOffsets, int FrameCount) BuildMetaspriteFrames(
        IndexedImage indexed, TilePlacement[] placements, int widthTiles, int heightTiles, int frameWidth, int frameHeight)
    {
        int framesAcross = widthTiles / frameWidth;
        int framesDown = heightTiles / frameHeight;

        var entries = new List<byte>();
        var frameOffsets = new List<int>(framesAcross * framesDown);

        for (int frameRow = 0; frameRow < framesDown; frameRow++)
        {
            for (int frameCol = 0; frameCol < framesAcross; frameCol++)
            {
                frameOffsets.Add(entries.Count / MetaspriteEntryBytes);

                for (int ly = 0; ly < frameHeight; ly++)
                {
                    for (int lx = 0; lx < frameWidth; lx++)
                    {
                        int tx = (frameCol * frameWidth) + lx;
                        int ty = (frameRow * frameHeight) + ly;
                        TilePlacement placement = placements[(ty * widthTiles) + tx];

                        if (placement.Index < 0)
                        {
                            continue;
                        }

                        byte props = (byte)(
                            (indexed.TilePalettes[(ty * widthTiles) + tx] & 0x07)
                            | (placement.FlipX ? 0x20 : 0)
                            | (placement.FlipY ? 0x40 : 0));

                        entries.Add((byte)(ly * GbTile.Size));
                        entries.Add((byte)(lx * GbTile.Size));
                        entries.Add((byte)placement.Index);
                        entries.Add(props);
                    }
                }

                entries.Add(unchecked((byte)MetaspriteEnd));
                entries.Add(0);
                entries.Add(0);
                entries.Add(0);
            }
        }

        return (entries.ToArray(), frameOffsets.ToArray(), framesAcross * framesDown);
    }

    /// <summary>
    /// Slices a tall-sprite sheet into 8x16 columns, top tile then bottom, and
    /// deduplicates whole columns rather than the tiles inside them.
    /// </summary>
    /// <remarks>
    /// Sprite rows outer, columns inner - the same reading order as the 8x8
    /// path's (ty, tx) loop - so the sheet still reads left-to-right,
    /// top-to-bottom by sprite position.
    /// </remarks>
    private static (IReadOnlyList<GbTile> Tiles, int SavedByFlip) SliceTallSpritePairs(
        IndexedImage indexed, int imageWidth, int widthTiles, int heightTiles, bool dedupeFlips)
    {
        var deduplicator = new TilePairDeduplicator(dedupeFlips);

        for (int py = 0; py < heightTiles / 2; py++)
        {
            for (int cx = 0; cx < widthTiles; cx++)
            {
                GbTile top = GbTile.Encode(TileIndices(indexed.PixelIndices, imageWidth, cx, py * 2));
                GbTile bottom = GbTile.Encode(TileIndices(indexed.PixelIndices, imageWidth, cx, (py * 2) + 1));
                deduplicator.Add(new TilePair(top, bottom));
            }
        }

        var tiles = new List<GbTile>(deduplicator.Unique.Count * 2);

        foreach (TilePair pair in deduplicator.Unique)
        {
            tiles.Add(pair.Top);
            tiles.Add(pair.Bottom);
        }

        return (tiles, deduplicator.SavedByFlip);
    }

    private static AssetArtifact Assemble(
        AssetRequest request,
        IndexedImage indexed,
        IReadOnlyList<GbTile> uniqueTiles,
        int savedByFlip,
        TilePlacement[]? placements,
        DecodedImage image,
        int widthTiles,
        int heightTiles,
        bool color,
        byte[]? metaspriteFrames = null,
        byte[]? frameOffsets = null,
        int frameCount = 0,
        byte[]? glyphTable = null)
    {
        var blobs = new List<AssetBlob>
        {
            new(AssetBlobRole.TileData, "_tiles", TileBytes(uniqueTiles)),
        };

        if (glyphTable is not null)
        {
            blobs.Add(new AssetBlob(AssetBlobRole.GlyphTable, "_glyph_table", glyphTable));
        }

        // A tileset is tiles alone; a sprite sheet's layout is the game's
        // business. Only a tile map needs the map and its attributes.
        if (request.Kind == AssetKind.TileMap)
        {
            blobs.Add(new AssetBlob(
                AssetBlobRole.MapIndices,
                "_map",
                Array.ConvertAll(placements!, p => (byte)p.Index)));

            if (color)
            {
                blobs.Add(new AssetBlob(AssetBlobRole.AttributeMap, "_attributes", Attributes(indexed, placements!)));
            }
        }

        if (color)
        {
            blobs.Add(new AssetBlob(
                AssetBlobRole.Palettes,
                "_palettes",
                indexed.Palettes.SelectMany(p => p.ToBgr555()).ToArray(),
                ElementWidth: 2));
        }

        if (metaspriteFrames is not null)
        {
            blobs.Add(new AssetBlob(AssetBlobRole.MetaspriteFrames, "_frames", metaspriteFrames));
            blobs.Add(new AssetBlob(AssetBlobRole.FrameOffsets, "_frame_offsets", frameOffsets!));
        }

        var stats = new AssetStats(
            image.Width,
            image.Height,
            widthTiles,
            heightTiles,
            widthTiles * heightTiles,
            uniqueTiles.Count,
            savedByFlip,
            color ? indexed.Palettes.Count : 0,
            frameCount);

        return new AssetArtifact(blobs, stats);
    }

    /// <summary>
    /// The Game Boy Color attribute byte for each map cell.
    /// </summary>
    /// <remarks>
    /// Bits 0-2 pick the palette, bit 5 flips horizontally, bit 6 vertically.
    /// Bit 3 would select VRAM bank 1, which GB# does not use yet.
    /// </remarks>
    private static byte[] Attributes(IndexedImage indexed, TilePlacement[] placements)
    {
        var attributes = new byte[placements.Length];

        for (int i = 0; i < placements.Length; i++)
        {
            TilePlacement placement = placements[i];

            attributes[i] = (byte)(
                (indexed.TilePalettes[i] & 0x07)
                | (placement.FlipX ? 0x20 : 0)
                | (placement.FlipY ? 0x40 : 0));
        }

        return attributes;
    }

    private static byte[] TileBytes(IReadOnlyList<GbTile> tiles)
    {
        var bytes = new byte[tiles.Count * GbTile.Bytes];

        for (int i = 0; i < tiles.Count; i++)
        {
            tiles[i].Data.CopyTo(bytes.AsSpan(i * GbTile.Bytes));
        }

        return bytes;
    }

    private static byte[] TileIndices(byte[] pixels, int imageWidth, int tileX, int tileY)
    {
        var indices = new byte[GbTile.Size * GbTile.Size];

        for (int y = 0; y < GbTile.Size; y++)
        {
            for (int x = 0; x < GbTile.Size; x++)
            {
                int px = (tileX * GbTile.Size) + x;
                int py = (tileY * GbTile.Size) + y;
                indices[(y * GbTile.Size) + x] = pixels[(py * imageWidth) + px];
            }
        }

        return indices;
    }

    private static bool UsesColor(DecodedImage image) =>
        image.Pixels.Any(p => p.R != p.G || p.G != p.B);

    private static void ReportPaletteFailure(
        PaletteFailure failure,
        AssetRequest request,
        DiagnosticBag diagnostics,
        string name)
    {
        switch (failure)
        {
            case TooManyColors many:
                diagnostics.Report(GBDiagnostics.TooManyColors, request.Span, name, many.Count);
                break;

            case TileTooManyColors tile:
                diagnostics.Report(
                    GBDiagnostics.TileTooManyColors,
                    request.Span,
                    tile.TileX,
                    tile.TileY,
                    name,
                    tile.Count);
                break;

            case TooManyPalettes palettes:
                diagnostics.Report(GBDiagnostics.TooManyPalettes, request.Span, name, palettes.Count);
                break;
        }
    }
}
