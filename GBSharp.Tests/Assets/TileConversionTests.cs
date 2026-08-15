using GBSharp.Assets.Images;
using GBSharp.Assets.Tiles;

namespace GBSharp.Tests.Assets;

/// <summary>
/// 2bpp encoding, deduplication and palettes.
/// </summary>
public sealed class TileConversionTests
{
    [Fact]
    public void EncodesBitplanesWithBitSevenLeftmost()
    {
        // The row 3,0,0,0,0,0,0,0 is colour 3 in the leftmost pixel, which sets
        // bit 7 of both planes. Hand-computed: this is the format VRAM wants,
        // and getting the bit order backwards would mirror every tile.
        var indices = new byte[64];
        indices[0] = 3;

        GbTile tile = GbTile.Encode(indices);

        Assert.Equal(0x80, tile.Data[0]);
        Assert.Equal(0x80, tile.Data[1]);
        Assert.Equal(0x00, tile.Data[2]);
    }

    [Fact]
    public void SplitsAColourAcrossTwoPlanes()
    {
        // Colour 1 sets the low plane only, colour 2 the high plane only.
        var indices = new byte[64];
        indices[0] = 1;
        indices[1] = 2;

        GbTile tile = GbTile.Encode(indices);

        Assert.Equal(0x80, tile.Data[0]);
        Assert.Equal(0x40, tile.Data[1]);
    }

    [Fact]
    public void EncodingRoundTrips()
    {
        var indices = new byte[64];

        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i % 4);
        }

        Assert.Equal(indices, GbTile.Encode(indices).ToIndices());
    }

    [Fact]
    public void IdenticalTilesShareOneCopy()
    {
        var deduplicator = new TileDeduplicator(dedupeFlips: false);

        GbTile a = Solid(1);
        GbTile b = Solid(2);

        Assert.Equal(0, deduplicator.Add(a).Index);
        Assert.Equal(1, deduplicator.Add(b).Index);
        Assert.Equal(0, deduplicator.Add(a).Index);
        Assert.Equal(2, deduplicator.Unique.Count);
    }

    [Fact]
    public void MirroredTilesShareOneCopyWhenFlipsAreAvailable()
    {
        var deduplicator = new TileDeduplicator(dedupeFlips: true);

        GbTile tile = Gradient();
        GbTile mirrored = tile.FlippedX();

        deduplicator.Add(tile);
        TilePlacement placement = deduplicator.Add(mirrored);

        Assert.Equal(0, placement.Index);
        Assert.True(placement.FlipX);
        Assert.False(placement.FlipY);
        Assert.Single(deduplicator.Unique);
        Assert.Equal(1, deduplicator.SavedByFlip);
    }

    [Fact]
    public void MirroredTilesStayDistinctWhenFlipsCannotBeRecorded()
    {
        // A monochrome background map has one byte per cell and nowhere to put
        // a flip bit. Sharing here would silently draw the wrong image.
        var deduplicator = new TileDeduplicator(dedupeFlips: false);

        GbTile tile = Gradient();
        deduplicator.Add(tile);
        deduplicator.Add(tile.FlippedX());

        Assert.Equal(2, deduplicator.Unique.Count);
        Assert.Equal(0, deduplicator.SavedByFlip);
    }

    [Fact]
    public void MonochromePalettesRunLightestFirst()
    {
        // Index 0 must be the lightest colour, because that is what the
        // hardware's default background palette expects.
        var image = new DecodedImage(8, 8, Fill(8, 8, [Black, White]), SourcePalette: null);

        IndexedImage? indexed = PaletteBuilder.BuildMonochrome(image, 1, 1, out PaletteFailure? failure);

        Assert.Null(failure);
        Assert.Equal(White, indexed!.Palettes[0].Colors[0]);
    }

    [Fact]
    public void AnIndexedImageKeepsItsOwnPaletteOrder()
    {
        Rgba32[] source = [Black, White];
        var image = new DecodedImage(8, 8, Fill(8, 8, [Black, White]), source);

        IndexedImage? indexed = PaletteBuilder.BuildMonochrome(image, 1, 1, out _);

        Assert.Equal(Black, indexed!.Palettes[0].Colors[0]);
    }

    [Fact]
    public void MoreThanFourColoursIsRejected()
    {
        Rgba32[] five =
        [
            Black, White, new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255),
        ];

        var image = new DecodedImage(8, 8, Fill(8, 8, five), SourcePalette: null);

        Assert.Null(PaletteBuilder.BuildMonochrome(image, 1, 1, out PaletteFailure? failure));
        Assert.Equal(5, Assert.IsType<TooManyColors>(failure).Count);
    }

    [Fact]
    public void ColoursAreEncodedAsBgr555()
    {
        // Pure red is 0x001F: five bits per channel with blue highest, which is
        // the reverse of the order the colour is written in.
        var palette = new GbPalette([new Rgba32(255, 0, 0, 255)]);
        byte[] bytes = palette.ToBgr555();

        Assert.Equal(0x1F, bytes[0]);
        Assert.Equal(0x00, bytes[1]);

        // Always padded to four entries, because the hardware reads four.
        Assert.Equal(8, bytes.Length);
    }

    [Fact]
    public void DisjointColoursThatFitTogetherShareOnePalette()
    {
        // Two colours each, four in total. Merging them is the right answer:
        // palettes are the scarce resource, and both tiles can draw from one.
        Rgba32[] left = [Black, White];
        Rgba32[] right = [new(255, 0, 0, 255), new(0, 255, 0, 255)];

        IndexedImage? indexed = TwoTiles(left, right, out PaletteFailure? failure);

        Assert.Null(failure);
        Assert.Single(indexed!.Palettes);
        Assert.Equal(indexed.TilePalettes[0], indexed.TilePalettes[1]);
    }

    [Fact]
    public void ColourPalettesAreAssignedPerTileWhenTheyCannotMerge()
    {
        // Three colours each and six in total, so they cannot share. Each tile
        // has to point at the palette that actually holds its own colours.
        Rgba32[] left = [Black, White, new(255, 0, 0, 255)];
        Rgba32[] right = [new(0, 255, 0, 255), new(0, 0, 255, 255), new(255, 255, 0, 255)];

        IndexedImage? indexed = TwoTiles(left, right, out PaletteFailure? failure);

        Assert.Null(failure);
        Assert.Equal(2, indexed!.Palettes.Count);
        Assert.NotEqual(indexed.TilePalettes[0], indexed.TilePalettes[1]);

        // And the assignment has to be the right way round.
        Assert.All(left, c => Assert.Contains(c, indexed.Palettes[indexed.TilePalettes[0]].Colors));
        Assert.All(right, c => Assert.Contains(c, indexed.Palettes[indexed.TilePalettes[1]].Colors));
    }

    /// <summary>Two 8x8 tiles side by side, each cycling through its own colours.</summary>
    private static IndexedImage? TwoTiles(Rgba32[] left, Rgba32[] right, out PaletteFailure? failure)
    {
        var pixels = new Rgba32[16 * 8];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Rgba32[] source = x < 8 ? left : right;
                pixels[(y * 16) + x] = source[(x + y) % source.Length];
            }
        }

        return PaletteBuilder.BuildColor(new DecodedImage(16, 8, pixels, SourcePalette: null), 2, 1, out failure);
    }

    [Fact]
    public void ATileUsingMoreThanFourColoursNamesItsPosition()
    {
        Rgba32[] five =
        [
            Black, White, new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255),
        ];

        var image = new DecodedImage(8, 8, Fill(8, 8, five), SourcePalette: null);

        Assert.Null(PaletteBuilder.BuildColor(image, 1, 1, out PaletteFailure? failure));
        Assert.Equal(5, Assert.IsType<TileTooManyColors>(failure).Count);
    }

    private static readonly Rgba32 Black = new(0, 0, 0, 255);
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static Rgba32[] Fill(int width, int height, Rgba32[] colors)
    {
        var pixels = new Rgba32[width * height];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = colors[i % colors.Length];
        }

        return pixels;
    }

    private static GbTile Solid(byte value)
    {
        var indices = new byte[64];
        Array.Fill(indices, value);
        return GbTile.Encode(indices);
    }

    /// <summary>A tile that is not its own mirror, so flips are detectable.</summary>
    private static GbTile Gradient()
    {
        var indices = new byte[64];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                indices[(y * 8) + x] = (byte)(x < 4 ? 0 : 3);
            }
        }

        return GbTile.Encode(indices);
    }
}
