using GBSharp.Assets.Images;
using GBSharp.Assets.Tiles;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Assets;

/// <summary>
/// The <c>[Font]</c> pipeline: sheet-shape validation, the 256-byte
/// ASCII-to-tile lookup, and the argument shape <see cref="Text"/> expands into.
/// </summary>
public sealed class FontAssetTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Dark = new(85, 85, 85, 255);

    private const string Characters = "AB";

    private const string LoadsAFont = $$"""
        using GB;

        public static class Program
        {
            [Font("font.png", Characters = "{{Characters}}")]
            private static FontAsset Alphabet;

            public static void Main()
            {
                Text.Load(Alphabet, 0);
            }
        }
        """;

    /// <summary>Two 8x8 glyphs, side by side, each a distinct solid shade so they never dedupe together.</summary>
    private static byte[] TwoGlyphSheet() => TestPng.Rgb(16, 8, (x, _) => x < GbTile.Size ? White : Dark);

    [Fact]
    public void AFontFieldConvertsIntoTilesAndAGlyphTable()
    {
        IRModule module = CompileWith(TwoGlyphSheet());

        IRGlobal tiles = module.Globals.Single(g => g.Name == "Program_Alphabet_tiles");
        ReadOnlyMemory<byte> tileBytes = Assert.IsType<IRDataBlob>(tiles.Initializer).Bytes;
        Assert.Equal(2 * GbTile.Bytes, tileBytes.Length);

        // No map, no attributes, no palettes: a font sheet is not a screen of
        // artwork and text draws with whatever attribute is already active.
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Alphabet_map");
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Alphabet_attributes");
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Alphabet_palettes");

        IRGlobal glyphTable = module.Globals.Single(g => g.Name == "Program_Alphabet_glyph_table");
        byte[] glyphBytes = Assert.IsType<IRDataBlob>(glyphTable.Initializer).Bytes.ToArray();

        Assert.Equal(256, glyphBytes.Length);

        // 'A' (65) is the sheet's left glyph, tile 0; 'B' (66) is the right one, tile 1.
        Assert.Equal(0, glyphBytes[(byte)'A']);
        Assert.Equal(1, glyphBytes[(byte)'B']);

        // Every code the font did not declare stays at 0 - drawing tile 0's
        // glyph rather than being checked at draw time (a build-time-only concern).
        Assert.Equal(0, glyphBytes[(byte)'Z']);
        Assert.Equal(0, glyphBytes[0]);
        Assert.Equal(0, glyphBytes[255]);
    }

    [Fact]
    public void LoadingExpandsIntoFourArguments()
    {
        IRModule module = CompileWith(TwoGlyphSheet());
        string ir = IRPrinter.Print(module);

        // tiles, glyph_table, tile_count, bank - the shape gbs_font_load
        // declares in gbs_runtime.h.
        Assert.Contains(
            "native gbs_font_load(Program_Alphabet_tiles, Program_Alphabet_glyph_table, ",
            ir);
    }

    [Fact]
    public void DrawingExpandsIntoTheSharedPrefixPlusItsOwnArguments()
    {
        const string source = $$"""
            using GB;

            public static class Program
            {
                [Font("font.png", Characters = "{{Characters}}")]
                private static FontAsset Alphabet;

                private static readonly byte[] Label = { 65, 66 };

                public static void Main()
                {
                    Text.Load(Alphabet, 0);
                    Text.Draw(Alphabet, 0, 1, 2, 2, Label);
                }
            }
            """;

        CompilationResult result = TestHarness.CompileWithAssets(
            source, new Dictionary<string, byte[]> { ["font.png"] = TwoGlyphSheet() });

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));

        string ir = IRPrinter.Print(result.Module!);
        const string prefix = "(Program_Alphabet_tiles, Program_Alphabet_glyph_table, ";

        Assert.Contains("native gbs_font_load" + prefix, ir);
        Assert.Contains("native gbs_font_draw" + prefix, ir);
    }

    [Fact]
    public void ASheetWiderThanTheCharacterSetIsReported()
    {
        // Three glyphs on the sheet, but only two declared characters.
        byte[] png = TestPng.Rgb(24, 8, (_, _) => Dark);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            LoadsAFont, new Dictionary<string, byte[]> { ["font.png"] = png }).Diagnostics;

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0627");
        Assert.Contains("3x1", reported.Message);
    }

    [Fact]
    public void ASheetTallerThanOneTileIsReported()
    {
        // Right width (two tiles), but two tiles tall instead of one.
        byte[] png = TestPng.Rgb(16, 16, (_, _) => Dark);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            LoadsAFont, new Dictionary<string, byte[]> { ["font.png"] = png }).Diagnostics;

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0627");
        Assert.Contains("2x2", reported.Message);
    }

    [Fact]
    public void EmptyCharactersIsReported()
    {
        const string source = """
            using GB;

            public static class Program
            {
                [Font("font.png")]
                private static FontAsset Alphabet;

                public static void Main()
                {
                    Text.Load(Alphabet, 0);
                }
            }
            """;

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            source, new Dictionary<string, byte[]> { ["font.png"] = TwoGlyphSheet() }).Diagnostics;

        TestHarness.AssertReported(diagnostics, "GBS0628");
    }

    [Fact]
    public void MaxTilesOnTheFontAttributeIsATighterBudget()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            $$"""
            using GB;

            public static class Program
            {
                [Font("font.png", Characters = "{{Characters}}", MaxTiles = 1)]
                private static FontAsset Alphabet;

                public static void Main()
                {
                    Text.Load(Alphabet, 0);
                }
            }
            """,
            new Dictionary<string, byte[]> { ["font.png"] = TwoGlyphSheet() }).Diagnostics;

        TestHarness.AssertReported(diagnostics, "GBS0604");
    }

    [Fact]
    public void FontConvertsAsMonochromeEvenOnGameBoyColor()
    {
        // A font never carries a palette blob, regardless of target: FontShape
        // has no room for one, and text draws with whatever attribute is
        // already active at the cell.
        IRModule module = CompileWith(TwoGlyphSheet(), AssetTargetProfile.GameBoyColor);

        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Alphabet_palettes");
    }

    [Fact]
    public void RepeatedGlyphsDeduplicateLikeAnyOtherTileset()
    {
        // Both declared characters are the identical solid tile.
        byte[] png = TestPng.Rgb(16, 8, (_, _) => Dark);

        IRModule module = CompileWith(png);
        IRAsset asset = Assert.Single(module.Assets);

        Assert.Equal(1, asset.Stats.UniqueTiles);

        IRGlobal glyphTable = module.Globals.Single(g => g.Name == "Program_Alphabet_glyph_table");
        byte[] glyphBytes = Assert.IsType<IRDataBlob>(glyphTable.Initializer).Bytes.ToArray();

        Assert.Equal(0, glyphBytes[(byte)'A']);
        Assert.Equal(0, glyphBytes[(byte)'B']);
    }

    private static IRModule CompileWith(byte[] png, AssetTargetProfile profile = AssetTargetProfile.GameBoy)
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAFont,
            new Dictionary<string, byte[]> { ["font.png"] = png },
            profile);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        return result.Module!;
    }
}
