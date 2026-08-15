using GBSharp.Assets.Images;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Tests.Assets;

/// <summary>
/// The <c>[Metasprite]</c> pipeline: frame-grid slicing, dropping fully
/// transparent sub-sprites, and the frame-offset table
/// <see cref="Metasprites"/> reads without a pointer array.
/// </summary>
/// <remarks>
/// The terminator byte values assumed here (<c>metasprite_end = -128</c>, so
/// <c>0x80</c>) come from the vendored GBDK install's own
/// <c>gb/metasprites.h</c>, not from a guess - see the handoff notes on why
/// that mattered enough to verify against the real header before writing
/// <c>gbs_runtime.c</c>.
/// </remarks>
public sealed class MetaspriteAssetTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Dark = new(85, 85, 85, 255);

    [Fact]
    public void LoadAndMoveShareTheSameEightArgumentPrefix()
    {
        IRModule module = CompileWith(TestPng.Rgb(8, 8, (_, _) => Dark), profile: AssetTargetProfile.GameBoyColor);
        string ir = IRPrinter.Print(module);

        const string prefix =
            "(Program_Hero_tiles, Program_Hero_palettes, Program_Hero_frames, Program_Hero_frame_offsets, ";

        Assert.Contains("native gbs_metasprite_load" + prefix, ir);
        Assert.Contains("native gbs_metasprite_move" + prefix, ir);
    }

    [Fact]
    public void TheSheetMustDivideEvenlyIntoFrames()
    {
        // 3 tiles wide; FrameWidth = 2 does not divide it.
        byte[] png = TestPng.Rgb(24, 8, (_, _) => Dark);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            MetaspriteSource(frameWidth: 2, frameHeight: 1),
            new Dictionary<string, byte[]> { ["hero.png"] = png }).Diagnostics;

        TestHarness.AssertReported(diagnostics, "GBS0625");
    }

    /// <summary>
    /// The point of a metasprite: an all-index-0 sub-sprite draws nothing on
    /// real hardware, so it costs no OAM entry and no frame-table bytes either.
    /// </summary>
    [Fact]
    public void AFullyTransparentSubSpriteIsOmittedFromItsFrame()
    {
        // One frame, two tiles: left is entirely the brightest colour (index 0,
        // so blank), right is the only other colour (index 1, so real).
        byte[] png = TestPng.Rgb(16, 8, (x, _) => x < GbTileSize ? White : Dark);

        IRModule module = CompileWith(png, frameWidth: 2, frameHeight: 1);

        byte[] frames = BytesOf(module, "Program_Hero_frames");
        byte[] offsets = BytesOf(module, "Program_Hero_frame_offsets");

        // dy=0, dx=8 (the right tile), tile=0 (the only tile that made it into
        // the deduplicated tileset), props=0 - then GBDK's own terminator record.
        Assert.Equal(new byte[] { 0, 8, 0, 0, 0x80, 0, 0, 0 }, frames);
        Assert.Equal(new byte[] { 0 }, offsets);
    }

    /// <summary>
    /// A frame that is entirely transparent is legal - some animations have an
    /// invisible frame - and costs nothing but its own terminator record.
    /// </summary>
    [Fact]
    public void FrameOffsetsAdvanceByTheEntryCountOfEachFrame()
    {
        // Two 1x1-tile frames: the first tile real, the second fully blank.
        byte[] png = TestPng.Rgb(16, 8, (x, _) => x < GbTileSize ? Dark : White);

        IRModule module = CompileWith(png, frameWidth: 1, frameHeight: 1);

        byte[] frames = BytesOf(module, "Program_Hero_frames");
        byte[] offsets = BytesOf(module, "Program_Hero_frame_offsets");

        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 0, 0x80, 0, 0, 0, // frame 0: one real entry, then the terminator
                0x80, 0, 0, 0,             // frame 1: nothing but the terminator
            },
            frames);

        Assert.Equal(new byte[] { 0, 2 }, offsets);
    }

    [Fact]
    public void IdenticalFramesShareOneTileInTheDeduplicatedSet()
    {
        // Frames 0 and 1 are the same solid colour; a third, all-white (and so
        // blank) tile is only here so Dark is not the sole colour in the image -
        // otherwise it would itself sort to index 0 and count as blank too.
        byte[] png = TestPng.Rgb(24, 8, (x, _) => x < GbTileSize * 2 ? Dark : White);

        IRModule module = CompileWith(png, frameWidth: 1, frameHeight: 1);

        IRAsset asset = Assert.Single(module.Assets);
        Assert.Equal(1, asset.Stats.UniqueTiles);

        byte[] frames = BytesOf(module, "Program_Hero_frames");
        Assert.Equal(0, frames[2]); // frame 0's tile index
        Assert.Equal(0, frames[6]); // frame 1's tile index - the same shared tile
    }

    [Fact]
    public void MaxTilesOnTheMetaspriteAttributeIsATighterBudget()
    {
        // Two distinct, non-blank, non-mirroring tiles - two frames' worth.
        byte[] png = TestPng.Rgb(16, 8, (x, y) => x < GbTileSize
            ? (x + y) % 2 == 0 ? Dark : White
            : (x + y) % 2 == 0 ? White : Dark);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            MetaspriteSource(frameWidth: 1, frameHeight: 1, maxTiles: 1),
            new Dictionary<string, byte[]> { ["hero.png"] = png }).Diagnostics;

        TestHarness.AssertReported(diagnostics, "GBS0604");
    }

    /// <summary>
    /// 128 one-tile frames, each a real (non-blank) sub-sprite: 128 x (1 real
    /// entry + 1 terminator) = 256 records, one past what a uint8_t offset
    /// table can index.
    /// </summary>
    [Fact]
    public void TooManySubSpritesAcrossAllFramesIsReported()
    {
        byte[] png = TestPng.Rgb(128, 64, (x, y) => x == 0 && y == 0 ? White : Dark);

        IReadOnlyList<GBDiagnostic> diagnostics = TestHarness.CompileWithAssets(
            MetaspriteSource(frameWidth: 1, frameHeight: 1),
            new Dictionary<string, byte[]> { ["hero.png"] = png }).Diagnostics;

        GBDiagnostic reported = TestHarness.AssertReported(diagnostics, "GBS0626");
        Assert.Contains("256", reported.Message);
    }

    private const int GbTileSize = 8;

    private static string MetaspriteSource(int frameWidth, int frameHeight, int maxTiles = 0) => $$"""
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            [Metasprite("hero.png", FrameWidth = {{frameWidth}}, FrameHeight = {{frameHeight}}, MaxTiles = {{maxTiles}})]
            private static MetaspriteAsset Hero;

            public static void Main()
            {
                Metasprites.Load(Hero);
                Metasprites.Move(Hero, 0, 0, 0, 80, 80);
            }
        }
        """;

    private static IRModule CompileWith(
        byte[] png,
        int frameWidth = 1,
        int frameHeight = 1,
        AssetTargetProfile profile = AssetTargetProfile.GameBoy)
    {
        CompilationResult result = TestHarness.CompileWithAssets(
            MetaspriteSource(frameWidth, frameHeight),
            new Dictionary<string, byte[]> { ["hero.png"] = png },
            profile);

        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
        return result.Module!;
    }

    private static byte[] BytesOf(IRModule module, string globalName) =>
        Assert.IsType<IRDataBlob>(module.Globals.Single(g => g.Name == globalName).Initializer).Bytes.ToArray();
}
