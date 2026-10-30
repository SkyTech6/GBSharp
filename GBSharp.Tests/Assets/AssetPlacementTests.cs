using GBSharp.Assets.Images;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.IR;
using GBSharp.Rules;

namespace GBSharp.Tests.Assets;

public sealed class AssetPlacementTests
{
    [Theory]
    [InlineData("Background.Load(Art, 254, 7);", true)]
    [InlineData("Background.Load(Art, 255, 7);", false)]
    [InlineData("Background.LoadGraphics(Art, 254, 7);", true)]
    [InlineData("Window.LoadGraphics(Art, 255, 7);", false)]
    [InlineData("Window.Load(Art, 254, 8);", false)]
    [InlineData("Background.CopyRegion(Art, 255, 255, 2, 1, 0, 0, 254, 7);", true)]
    [InlineData("Background.CopyRegion(Art, 0, 0, 2, 1, 1, 0, 254, 7);", false)]
    [InlineData("Window.CopyRegion(Art, 0, 0, 1, 2, 0, 0, 254, 7);", false)]
    public void ConstantPlacementsUseConvertedCountsAndWideSums(string call, bool valid)
    {
        CompilationResult result = TestHarness.CompileWithAssets(Source(call), Images(), AssetTargetProfile.GameBoyColor);
        if (valid)
            Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        else
            TestHarness.AssertReported(result.Diagnostics, "GBS0629");
    }

    [Theory]
    [InlineData("Move", "move_at")]
    [InlineData("MoveFlippedX", "move_flip_x_at")]
    [InlineData("MoveFlippedY", "move_flip_y_at")]
    [InlineData("MoveFlippedXy", "move_flip_xy_at")]
    public void MetaspriteOffsetIsOneAdditionalArgumentAfterTheUnchangedPrefix(string method, string native)
    {
        string source = $$"""
            using GB;
            public static class Program {
                [Metasprite("art.png", FrameWidth = 1, FrameHeight = 1)] static MetaspriteAsset Hero;
                public static void Main() {
                    Metasprites.Load(Hero, 192, 4);
                    Metasprites.{{method}}(Hero, 0, 192, 8, 80, 80, 4);
                }
            }
            """;
        CompilationResult result = TestHarness.CompileWithAssets(source, Images(), AssetTargetProfile.GameBoyColor);
        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        string ir = IRPrinter.Print(result.Module!);
        Assert.Contains("native gbs_metasprite_load_at(", ir);
        Assert.Contains("native gbs_metasprite_" + native + "(", ir);
        Assert.Contains("192, 8, 80, 80, 4)", ir);
    }

    [Fact]
    public void TallSpriteConstantPlacementMustBeEven()
    {
        string source = """
            using GB;
            using static GB.Hardware;
            public static class Program {
                [Sprite("art.png", TallSprites = true)] static SpriteAsset Hero;
                public static void Main() { Sprites.Load(Hero, 191, 0); }
            }
            """;
        var images = new Dictionary<string, byte[]>();
        // Four shades keep this a valid DMG conversion and isolate the alignment rule.
        images["art.png"] = TestPng.Rgb(8, 16, (_, y) => y < 8 ? new Rgba32(85, 85, 85, 255) : new Rgba32(0, 0, 0, 255));
        CompilationResult result = TestHarness.CompileWithAssets(source, images);
        TestHarness.AssertReported(result.Diagnostics, "GBS0629");
    }

    [Fact]
    public void PlacementRuleRequiresConvertedAssetsAndIsIntentionallyAbsentFromIdeCatalog() =>
        Assert.DoesNotContain(GBRuleCatalog.IdeReportable, d => d.Id == "GBS0629");

    [Theory]
    [InlineData(38, true)]
    [InlineData(39, false)]
    public void ConstantOamRangeUsesTheSelectedFramesActualSpriteCount(int first, bool valid)
    {
        string source = $$"""
            using GB;
            public static class Program {
                [Metasprite("art.png", FrameWidth = 2, FrameHeight = 1)] static MetaspriteAsset Hero;
                public static void Main() { Metasprites.Move(Hero, 0, 192, {{first}}, 80, 80, 0); }
            }
            """;
        var images = new Dictionary<string, byte[]>
        {
            ["art.png"] = TestPng.Rgb(16, 8, (x, _) => x % 8 < 4 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 255)),
        };
        CompilationResult result = TestHarness.CompileWithAssets(source, images);
        if (valid) Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        else TestHarness.AssertReported(result.Diagnostics, "GBS0629");
    }

    private static string Source(string call) => $$"""
        using GB;
        public static class Program {
            [Asset("art.png")] static TileMap Art;
            public static void Main() { {{call}} }
        }
        """;

    private static Dictionary<string, byte[]> Images() => new()
    {
        ["art.png"] = TestPng.Rgb(16, 8, (x, _) => x < 8 ? new Rgba32(85, 85, 85, 255) : new Rgba32(0, 0, 0, 255)),
    };
}
