using GBSharp.Assets.Images;
using GBSharp.Assets.Tiles;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Assets;

/// <summary>
/// Pins today's <c>[Sprite]</c> pipeline behaviour before it changes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this suite exercised <c>SpriteAsset</c> before this file: every
/// case here is new ground, not a change to an existing test. That matters
/// because the pipeline is about to grow 8x16 pairing (thesis handoff item 1),
/// and a sheet's tile order is invisible in a passing test but obvious in the
/// bytes: these tests read the bytes.
/// </para>
/// <para>
/// <see cref="RowMajorSlicingIsTodaysOrderForShortSprites"/> in particular pins
/// the documented bug: <c>GBSharp.Framework/Assets.cs</c> claims sheets slice
/// "in the order the hardware expects", which is only true in 8x8 mode. This
/// test locks down that current (short-sprite) behaviour stays row-major once
/// 8x16 mode is added, rather than accidentally changing both at once.
/// </para>
/// </remarks>
public sealed class SpriteAssetTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Light = new(170, 170, 170, 255);
    private static readonly Rgba32 Dark = new(85, 85, 85, 255);
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    private const string LoadsASprite = """
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            [Sprite("hero.png")]
            private static SpriteAsset Hero;

            public static void Main()
            {
                Sprites.Load(Hero);
            }
        }
        """;

    private const string TallSpriteSource = """
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            [Sprite("hero.png", TallSprites = true)]
            private static SpriteAsset Hero;

            public static void Main()
            {
                Display.UseTallSprites();
                Sprites.Load(Hero);
            }
        }
        """;

    [Fact]
    public void ASpriteFieldConvertsIntoTilesOnly()
    {
        // A sprite sheet's layout is the game's business: unlike a TileMap there
        // is no map or attribute blob, only the tile data (and palettes on GBC).
        IRModule module = CompileWith(FlatQuadrants());

        Assert.Contains(module.Globals, g => g.Name == "Program_Hero_tiles");
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Hero_map");
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Hero_attributes");
    }

    [Fact]
    public void LoadingExpandsIntoFiveArguments()
    {
        IRModule module = CompileWith(FlatQuadrants(), AssetTargetProfile.GameBoyColor);
        string ir = IRPrinter.Print(module);

        // tiles, palettes, tile_count, palette_count, bank - the shape gbs_sprite_load
        // declares in gbs_runtime.h. Changing the order on one side without the
        // other compiles fine and loads the wrong data.
        Assert.Contains(
            "native gbs_sprite_load(Program_Hero_tiles, Program_Hero_palettes, ",
            ir);
    }

    /// <summary>
    /// Today, a sheet slices into 8x8 tiles row-major: top-left, top-right,
    /// bottom-left, bottom-right. That is correct for 8x8 sprites and is what
    /// this test locks down; it is documented as wrong for 8x16 sprites, which
    /// need the pair (2n, 2n+1) to be (top, bottom) of the same column instead.
    /// </summary>
    [Fact]
    public void RowMajorSlicingIsTodaysOrderForShortSprites()
    {
        IRModule module = CompileWith(FlatQuadrants());

        IRGlobal tiles = module.Globals.Single(g => g.Name == "Program_Hero_tiles");
        ReadOnlyMemory<byte> bytes = Assert.IsType<IRDataBlob>(tiles.Initializer).Bytes;

        Assert.Equal(4 * GbTile.Bytes, bytes.Length);

        byte[] topLeft = GbTile.Encode(Checkerboard(0, 1)).Data.ToArray();
        byte[] topRight = GbTile.Encode(Checkerboard(1, 2)).Data.ToArray();
        byte[] bottomLeft = GbTile.Encode(Checkerboard(2, 3)).Data.ToArray();
        byte[] bottomRight = GbTile.Encode(Checkerboard(3, 0)).Data.ToArray();

        Assert.Equal(topLeft, bytes.Slice(0 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(topRight, bytes.Slice(1 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(bottomLeft, bytes.Slice(2 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(bottomRight, bytes.Slice(3 * GbTile.Bytes, GbTile.Bytes).ToArray());
    }

    /// <summary>
    /// SpriteAttribute's own remarks: "flipped duplicates always share one copy
    /// because OAM carries flip bits", on, with no DedupeFlips option to set,
    /// unlike a background where it is a caller decision.
    /// </summary>
    [Fact]
    public void FlipDeduplicationDefaultsOnForSprites()
    {
        // Two 8x8 tiles, side by side, the second a horizontal mirror of the
        // first. Even on a monochrome target this must collapse to one tile.
        byte[] png = TestPng.Rgb(16, 8, (x, y) =>
        {
            int localX = x % GbTile.Size;
            bool rightHalf = x >= GbTile.Size;
            int effectiveX = rightHalf ? GbTile.Size - 1 - localX : localX;
            return (effectiveX + y) % 2 == 0 ? White : Dark;
        });

        IRModule module = CompileWith(png);
        IRAsset asset = Assert.Single(module.Assets);

        Assert.Equal(1, asset.Stats.UniqueTiles);
        Assert.Equal(1, asset.Stats.TilesSavedByFlip);
    }

    /// <summary>
    /// The fix for the documented bug: the exact same sheet as
    /// <see cref="RowMajorSlicingIsTodaysOrderForShortSprites"/>, but with
    /// TallSprites on. Row-major would put column 0's top and column 1's top
    /// adjacent (both even tile indices); the hardware needs column 0's top and
    /// bottom adjacent instead, so the order must become column-major.
    /// </summary>
    [Fact]
    public void TallSpritesPairColumnsInsteadOfRows()
    {
        IRModule module = CompileTallSprite(FlatQuadrants());

        IRGlobal tiles = module.Globals.Single(g => g.Name == "Program_Hero_tiles");
        ReadOnlyMemory<byte> bytes = Assert.IsType<IRDataBlob>(tiles.Initializer).Bytes;

        Assert.Equal(4 * GbTile.Bytes, bytes.Length);

        // Column 0 is (top-left, bottom-left); column 1 is (top-right, bottom-right).
        byte[] column0Top = GbTile.Encode(Checkerboard(0, 1)).Data.ToArray();
        byte[] column0Bottom = GbTile.Encode(Checkerboard(2, 3)).Data.ToArray();
        byte[] column1Top = GbTile.Encode(Checkerboard(1, 2)).Data.ToArray();
        byte[] column1Bottom = GbTile.Encode(Checkerboard(3, 0)).Data.ToArray();

        Assert.Equal(column0Top, bytes.Slice(0 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(column0Bottom, bytes.Slice(1 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(column1Top, bytes.Slice(2 * GbTile.Bytes, GbTile.Bytes).ToArray());
        Assert.Equal(column1Bottom, bytes.Slice(3 * GbTile.Bytes, GbTile.Bytes).ToArray());
    }

    [Fact]
    public void TallSpritesRequireAHeightDivisibleBySixteen()
    {
        // 8x8: tile-aligned, but only one tile tall, so no 8x16 column fits.
        byte[] png = TestPng.Rgb(GbTile.Size, GbTile.Size, (_, _) => White);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            TallSpriteSource,
            new Dictionary<string, byte[]> { ["hero.png"] = png }).Diagnostics;

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0624");
        Assert.Contains("16", reported.Message);
    }

    /// <summary>
    /// Two identical 8x16 sprites must collapse to one pair (two tiles in ROM),
    /// not zero savings because their tiles happen to interleave with another
    /// column's when deduplicated one tile at a time.
    /// </summary>
    [Fact]
    public void IdenticalColumnsDeduplicateAsAPair()
    {
        byte[] png = TestPng.Rgb(16, 16, (x, y) =>
        {
            int localX = x % GbTile.Size;
            return Shade((localX + y) % 2 == 0 ? 0 : 1);
        });

        IRModule module = CompileTallSprite(png);
        IRAsset asset = Assert.Single(module.Assets);

        // One pair, two tiles - not four, and not merged down to one tile.
        Assert.Equal(2, asset.Stats.UniqueTiles);
    }

    [Fact]
    public void MaxTilesOnTheSpriteAttributeIsATighterBudget()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            """
            using GB;
            using static GB.Hardware;

            public static class Program
            {
                [Sprite("hero.png", MaxTiles = 2)]
                private static SpriteAsset Hero;

                public static void Main()
                {
                    Sprites.Load(Hero);
                }
            }
            """,
            new Dictionary<string, byte[]> { ["hero.png"] = FlatQuadrants() }).Diagnostics;

        TestHarness.AssertReported(diagnostics, "GBS0604");
    }

    /// <summary>Four distinct, mutually non-mirroring 8x8 tiles in a 2x2 grid.</summary>
    private static byte[] FlatQuadrants() => TestPng.Rgb(16, 16, (x, y) =>
    {
        int quadrant = ((x / GbTile.Size) * 1) + ((y / GbTile.Size) * 2);
        (int evenIndex, int oddIndex) = quadrant switch
        {
            0 => (0, 1), // top-left
            1 => (1, 2), // top-right
            2 => (2, 3), // bottom-left
            _ => (3, 0), // bottom-right
        };

        return Shade((x + y) % 2 == 0 ? evenIndex : oddIndex);
    });

    private static byte[] Checkerboard(int evenIndex, int oddIndex)
    {
        var indices = new byte[GbTile.Size * GbTile.Size];

        for (int y = 0; y < GbTile.Size; y++)
        {
            for (int x = 0; x < GbTile.Size; x++)
            {
                indices[(y * GbTile.Size) + x] = (byte)((x + y) % 2 == 0 ? evenIndex : oddIndex);
            }
        }

        return indices;
    }

    private static Rgba32 Shade(int index) => index switch
    {
        0 => White,
        1 => Light,
        2 => Dark,
        _ => Black,
    };

    private static IRModule CompileWith(byte[] png, AssetTargetProfile profile = AssetTargetProfile.GameBoy)
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsASprite,
            new Dictionary<string, byte[]> { ["hero.png"] = png },
            profile);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        return result.Module!;
    }

    private static IRModule CompileTallSprite(byte[] png)
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            TallSpriteSource,
            new Dictionary<string, byte[]> { ["hero.png"] = png });

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        return result.Module!;
    }
}
