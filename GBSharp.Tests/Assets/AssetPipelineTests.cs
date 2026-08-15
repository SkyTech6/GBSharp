using GBSharp.Assets.Images;
using GBSharp.Assets.Tiles;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Assets;

/// <summary>
/// An image on disk becoming ROM data, through the real compiler.
/// </summary>
public sealed class AssetPipelineTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Light = new(170, 170, 170, 255);
    private static readonly Rgba32 Dark = new(85, 85, 85, 255);
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    private const string LoadsAnAsset = """
        using GB;

        public static class Program
        {
            [Asset("art.png")]
            private static TileMap Art;

            public static void Main()
            {
                Background.Load(Art);
            }
        }
        """;

    /// <summary>Four shades in a pattern that repeats every tile.</summary>
    private static byte[] FourShades(int width = 32, int height = 16) =>
        TestPng.Rgb(width, height, (x, y) => (((x / 4) + (y / 4)) % 4) switch
        {
            0 => White,
            1 => Light,
            2 => Dark,
            _ => Black,
        });

    /// <summary>
    /// A user's own type called TileMap is theirs, not the framework's.
    /// </summary>
    /// <remarks>
    /// The asset kind used to be selected by comparing the field's type name,
    /// so any struct named TileMap in any namespace was handed to the image
    /// pipeline. Matching the resolved framework symbol instead means this
    /// declaration is rejected as an invalid asset rather than quietly
    /// converting a PNG into someone else's type.
    /// </remarks>
    [Fact]
    public void AUserTypeNamedLikeAnAssetMarkerIsNotAnAsset()
    {
        CompilationResult result = TestHarness.CompileWithAssets("""
            using GB;

            public struct TileMap
            {
                public byte Width;
            }

            public static class Program
            {
                [Asset("art.png")]
                private static TileMap Art;

                public static void Main()
                {
                    Display.Enable();
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades() });

        TestHarness.AssertReported(result.Diagnostics, "GBS0609");
    }

    /// <summary>
    /// A world larger than the hardware's 32x32 map is legal, and says so.
    /// </summary>
    /// <remarks>
    /// The hardware map is what fits on screen plus a margin; a scrolling world
    /// is larger than that by definition. GB# converts it and points at
    /// DrawRegion, rather than refusing an image that is the normal shape for the
    /// thing being built.
    /// </remarks>
    [Fact]
    public void AMapLargerThanTheHardwareMapIsConvertedWithANotice()
    {
        // 40x24 tiles: wider and taller than 32, well inside the 128 ceiling.
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAnAsset,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades(320, 192) },
            AssetTargetProfile.GameBoyColor);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));

        GBDiagnostic notice = TestHarness.AssertReported(result.Diagnostics, "GBS0623");
        Assert.Equal(GBSeverity.Info, notice.Severity);

        IRAsset asset = Assert.Single(result.Module!.Assets);
        Assert.Equal(40, asset.Stats.WidthTiles);
        Assert.Equal(24, asset.Stats.HeightTiles);
    }

    [Fact]
    public void AMapWiderThanOneRomBankIsRefused()
    {
        // 136 tiles a side is past the 128 GB# will place.
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAnAsset,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades(1088, 64) },
            AssetTargetProfile.GameBoyColor);

        GBDiagnostic error = TestHarness.AssertReported(result.Diagnostics, "GBS0611");
        Assert.Contains("128", error.Message);
    }

    /// <summary>
    /// Two assets that each fit and cannot coexist is the failure a per-file
    /// asset tool cannot see. GB# sees the whole program, so it can.
    /// </summary>
    [Fact]
    public void TilesAreCountedAcrossEveryBackgroundAsset()
    {
        CompilationResult result = TestHarness.CompileWithAssets("""
            using GB;

            public static class Program
            {
                [Asset("one.png")]
                private static TileMap One;

                [Asset("two.png")]
                private static TileMap Two;

                public static void Main()
                {
                    Background.Load(One);
                    Background.Load(Two);
                }
            }
            """,
            new Dictionary<string, byte[]>
            {
                // Distinct content, so they do not deduplicate into one copy.
                ["one.png"] = FourShades(64, 64),
                ["two.png"] = TestPng.Rgb(64, 64, (x, y) => (((x / 8) * (y / 8)) % 4) switch
                {
                    0 => White,
                    1 => Dark,
                    2 => Light,
                    _ => Black,
                }),
            },
            AssetTargetProfile.GameBoyColor);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));

        GBDiagnostic note = TestHarness.AssertReported(result.Diagnostics, "GBS0204");

        int total = result.Module!.Assets.Sum(a => a.Stats.UniqueTiles);
        Assert.Contains(total.ToString(), note.Message);
    }

    /// <summary>
    /// Every tile unique and flip-proof: row 0 all white and row 7 all black
    /// defeat vertical-flip dedup, a light right half defeats horizontal, and
    /// rows 1..6 encode the tile index two bits at a time on the left.
    /// </summary>
    private static byte[] UniqueTiles(int widthTiles, int heightTiles, int indexOffset) =>
        TestPng.Rgb(widthTiles * 8, heightTiles * 8, (x, y) =>
        {
            int row = y % 8;
            if (row == 0)
            {
                return White;
            }
            if (row == 7)
            {
                return Black;
            }
            if (x % 8 >= 4)
            {
                return Light;
            }
            int tileIndex = indexOffset + (y / 8) * widthTiles + (x / 8);
            return ((tileIndex >> ((row - 1) * 2)) & 3) switch
            {
                0 => White,
                1 => Light,
                2 => Dark,
                _ => Black,
            };
        });

    [Fact]
    public void ScreensThatReplaceEachOtherMayExceedTheRegionTogether()
    {
        // Two screens totalling 400 unique tiles: impossible in VRAM at once,
        // routine when one replaces the other (each Background.Load rebases at
        // tile 0). The sum stays a GBS0204 note; only a single asset larger
        // than the region would be the GBS0205 error.
        CompilationResult result = TestHarness.CompileWithAssets("""
            using GB;

            public static class Program
            {
                [Asset("one.png")]
                private static TileMap One;

                [Asset("two.png")]
                private static TileMap Two;

                public static void Main()
                {
                    Background.Load(One);
                    Background.Load(Two);
                }
            }
            """,
            new Dictionary<string, byte[]>
            {
                ["one.png"] = UniqueTiles(20, 10, 0),
                ["two.png"] = UniqueTiles(20, 10, 300),
            },
            AssetTargetProfile.GameBoyColor);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        TestHarness.AssertNotReported(result.Diagnostics, "GBS0205");

        GBDiagnostic note = TestHarness.AssertReported(result.Diagnostics, "GBS0204");
        Assert.Contains("400", note.Message);
    }

    [Fact]
    public void OneAssetAloneGetsNoWholeProgramTileNote()
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAnAsset,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades() });

        // Its own budget already covers it; a sum of one says nothing new.
        TestHarness.AssertNotReported(result.Diagnostics, "GBS0204");
    }

    [Fact]
    public void AMapThatFitsTheHardwareMapGetsNoNotice()
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAnAsset,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades() });

        TestHarness.AssertNotReported(result.Diagnostics, "GBS0623");
    }

    [Fact]
    public void AnImageBecomesReadOnlyRomData()
    {
        IRModule module = CompileWith(FourShades());

        Assert.All(module.Globals.Where(g => g.Name.StartsWith("Program_Art", StringComparison.Ordinal)),
            g => Assert.True(g.IsReadOnly, $"{g.Name} should be in ROM, not WRAM"));

        IRGlobal tiles = module.Globals.Single(g => g.Name == "Program_Art_tiles");
        IRGlobal map = module.Globals.Single(g => g.Name == "Program_Art_map");

        Assert.IsType<IRDataBlob>(tiles.Initializer);
        Assert.Equal(4 * 2, Assert.IsType<IRArrayType>(map.Type).Length);
    }

    [Fact]
    public void TheAssetFieldItselfReservesNoMemory()
    {
        // It names data in ROM. Reporting it as a WRAM allocation, or emitting
        // a global for the handle, would both be wrong.
        IRModule module = CompileWith(FourShades());

        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Art");
    }

    [Fact]
    public void LoadingExpandsIntoPointersAndSizes()
    {
        IRModule module = CompileWith(FourShades());
        string ir = IRPrinter.Print(module);

        // One C# argument, eight C arguments: the tables and the sizes that go
        // with them, filled in from the image.
        Assert.Contains("native gbs_background_load(Program_Art_tiles, Program_Art_map,", ir);
    }

    [Fact]
    public void ColourTargetsAlsoGetAttributesAndPalettes()
    {
        IRModule module = CompileWith(FourShades(), AssetTargetProfile.GameBoyColor);

        Assert.Contains(module.Globals, g => g.Name == "Program_Art_attributes");
        Assert.Contains(module.Globals, g => g.Name == "Program_Art_palettes");
    }

    [Fact]
    public void MonochromeTargetsGetNeither()
    {
        // A DMG has no attribute map and no colour palettes, so generating them
        // would be ROM spent on data the machine cannot read.
        IRModule module = CompileWith(FourShades());

        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Art_attributes");
        Assert.DoesNotContain(module.Globals, g => g.Name == "Program_Art_palettes");
    }

    [Fact]
    public void RepeatedArtworkCollapses()
    {
        IRModule module = CompileWith(FourShades(64, 64));

        IRAsset asset = Assert.Single(module.Assets);

        Assert.Equal(64, asset.Stats.TotalTiles);
        Assert.True(
            asset.Stats.UniqueTiles < asset.Stats.TotalTiles,
            $"deduplication should have saved something: {asset.Stats.UniqueTiles} of {asset.Stats.TotalTiles}");
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        // CI builds on Windows and Linux and uploads the ROMs. If conversion
        // depended on hash iteration order the two would silently differ.
        byte[] png = FourShades(64, 64);

        string First()
        {
            IRModule module = CompileWith(png, AssetTargetProfile.GameBoyColor);
            IRGlobal tiles = module.Globals.Single(g => g.Name == "Program_Art_tiles");
            return Convert.ToHexString(Assert.IsType<IRDataBlob>(tiles.Initializer).Bytes.Span);
        }

        Assert.Equal(First(), First());
    }

    [Fact]
    public void AnAssetCannotBeUsedAsAValue()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = DiagnosticsFor(
            """
            using GB;

            public static class Program
            {
                [Asset("art.png")]
                private static TileMap Art;

                private static TileMap Copy;

                public static void Main()
                {
                    Copy = Art;
                }
            }
            """,
            FourShades());

        TestHarness.AssertReported(diagnostics, "GBS0613");
    }

    [Fact]
    public void TooManyColoursIsReportedAgainstTheDeclaration()
    {
        // The message from thesis section 14, pointing at C# rather than at the
        // image, which is the whole reason conversion happens in the compiler.
        Rgba32[] six =
        [
            White, Light, Dark, Black, new(255, 0, 0, 255), new(0, 255, 0, 255),
        ];

        byte[] png = TestPng.Rgb(16, 8, (x, _) => six[x % 6]);

        GBDiagnostic reported = TestHarness.AssertReported(DiagnosticsFor(LoadsAnAsset, png), "GBS0601");

        Assert.Contains("6 colours", reported.Message);
        Assert.Contains("Program.cs", reported.Span.FilePath);

        // Line 6 is 'private static TileMap Art;'. An image problem has to land
        // on the developer's own source, not on the image.
        Assert.Equal(6, reported.Span.Line);
    }

    [Fact]
    public void DimensionsMustBeTileAligned()
    {
        byte[] png = TestPng.Rgb(17, 8, (_, _) => White);

        TestHarness.AssertReported(DiagnosticsFor(LoadsAnAsset, png), "GBS0605");
    }

    [Fact]
    public void AMissingImageListsWhereItLooked()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            TestHarness.CompileWithAssets(LoadsAnAsset, new Dictionary<string, byte[]>()).Diagnostics,
            "GBS0606");

        Assert.Contains("art.png", reported.Message);
    }

    [Fact]
    public void AnUnreadableImageIsADiagnosticNotACrash()
    {
        TestHarness.AssertReported(DiagnosticsFor(LoadsAnAsset, "this is not a png"u8.ToArray()), "GBS0608");
    }

    [Fact]
    public void InterlacingNamesTheFeatureAndTheFix()
    {
        GBDiagnostic reported = TestHarness.AssertReported(
            DiagnosticsFor(LoadsAnAsset, TestPng.Interlaced(16, 16)),
            "GBS0607");

        Assert.Contains("interlac", reported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-interlaced", reported.Descriptor.Help!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlipDeduplicationIsRefusedWhereItCannotBeRecorded()
    {
        IReadOnlyList<GBDiagnostic> diagnostics = DiagnosticsFor(
            """
            using GB;

            public static class Program
            {
                [Asset("art.png", DedupeFlips = true)]
                private static TileMap Art;

                public static void Main()
                {
                    Background.Load(Art);
                }
            }
            """,
            FourShades());

        TestHarness.AssertReported(diagnostics, "GBS0614");
    }

    [Fact]
    public void TwoFieldsNamingTheSameImageShareOneCopy()
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            """
            using GB;

            public static class Program
            {
                [Asset("art.png")]
                private static TileMap First;

                [Asset("art.png")]
                private static TileMap Second;

                public static void Main()
                {
                    Background.Load(First);
                    Background.Load(Second);
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = FourShades() });

        TestHarness.AssertReported(result.Diagnostics, "GBS0621");

        // One copy in ROM, and both loads reach it.
        Assert.Single(result.Module!.Globals, g => g.Name == "Program_First_tiles");
        Assert.DoesNotContain(result.Module.Globals, g => g.Name == "Program_Second_tiles");
    }

    [Fact]
    public void TheTileBudgetIsTheHardwaresOwnLimit()
    {
        // set_bkg_data counts tiles in a uint8_t, so 255 is the ceiling. This
        // image is 256 distinct tiles by construction.
        byte[] png = TestPng.Rgb(256 * GbTile.Size, GbTile.Size, (x, y) =>
        {
            int tile = x / GbTile.Size;
            int px = x % GbTile.Size;
            return (tile >> (px % 8) & 1) == 1 || y % 8 == tile % 8 ? Black : White;
        });

        IReadOnlyList<GBDiagnostic> diagnostics = DiagnosticsFor(LoadsAnAsset, png);

        // Either the budget or the map size stops it; both are real limits and
        // both name a number the developer can act on.
        Assert.Contains(diagnostics, d => d.Id is "GBS0604" or "GBS0611");
    }

    private static IRModule CompileWith(byte[] png, AssetTargetProfile profile = AssetTargetProfile.GameBoy)
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            LoadsAnAsset,
            new Dictionary<string, byte[]> { ["art.png"] = png },
            profile);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        return result.Module!;
    }

    private static IReadOnlyList<GBDiagnostic> DiagnosticsFor(string source, byte[] png) =>
        TestHarness.CompileWithAssets(source, new Dictionary<string, byte[]> { ["art.png"] = png }).Diagnostics;
}
