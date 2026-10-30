using GB;
using GBSharp.Assets.Images;
using GBSharp.Backend.GBDK;
using GBSharp.Compiler.IR;
using GBSharp.Compiler.Assets;
using GBSharp.Tests.Assets;

namespace GBSharp.Tests.Integration;

public sealed class AssetPlacementRomTests
{
    [Theory]
    [InlineData(GBTarget.GameBoy)]
    [InlineData(GBTarget.GameBoyColor)]
    public void BankedMapCopiesWrapPreserveGraphicsAndRestoreTheCallingBank(GBTarget target)
    {
        if (!TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable) return;
        string source = """
            using GB;
            using static GB.Hardware;
            public static class ArtData {
                [Bank(2), Asset("art.png", DedupeFlips = true)] public static TileMap Art;
            }
            [Bank(3)] public static class Updates {
                public static byte Copy() {
                    Background.CopyRegion(ArtData.Art, 31, 31, 2, 1, 1, 0, 64, 2);
                    Background.CopyRegion(ArtData.Art, 2, 31, 1, 1, 2, 0, 64, 2);
                    Window.CopyRegion(ArtData.Art, 31, 31, 2, 1, 1, 0, 100, 5);
                    byte invalidTile = 255;
                    byte invalidPalette = 8;
                    byte invalidWidth = 33;
                    Background.LoadGraphics(ArtData.Art, invalidTile, 2);
                    Window.LoadGraphics(ArtData.Art, 0, invalidPalette);
                    Background.CopyRegion(ArtData.Art, 3, 31, 2, 1, 1, 0, invalidTile, 2);
                    Window.CopyRegion(ArtData.Art, 3, 31, invalidWidth, 1, 0, 0, 100, 5);
                    return 3;
                }
            }
            public static class Program {
                public static void Main() {
                    Display.Disable();
                    Background.LoadGraphics(ArtData.Art, 64, 2);
                    Window.LoadGraphics(ArtData.Art, 100, 5);
                    Background.SetTile(3, 31, 17);
                    Display.Enable();
                    while (true) {
                        if (Input.A) {
                            Display.Disable();
                            Sprites[39].Tile = Updates.Copy();
                            Display.Enable();
                        }
                        Game.WaitVBlank();
                    }
                }
            }
            """;
        if (target == GBTarget.GameBoy) source = source.Replace("DedupeFlips = true", "DedupeFlips = false", StringComparison.Ordinal);
        var images = Images();
        var compilation = TestHarness.CompileWithAssets(source, images,
            target == GBTarget.GameBoyColor ? AssetTargetProfile.GameBoyColor : AssetTargetProfile.GameBoy);
        Assert.True(compilation.Succeeded, TestHarness.Describe(compilation.Diagnostics));
        byte[] map = Blob(compilation.Module!, "_map");
        byte[]? attrs = target == GBTarget.GameBoyColor ? Blob(compilation.Module!, "_attributes") : null;
        var build = TestHarness.BuildRomWithAssets(source, images, target);
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        using var game = GameBoyTest.Load(build.RomPath!);
        game.RunFrames(15);
        byte lcd = game.ReadMemory(0xff40);
        game.WriteMemory(0xff40, (byte)(lcd & 0x7f));
        // Make a forgotten tileset upload observable even when the uploaded data
        // would otherwise be identical to initialization.
        game.WriteMemory(0xff4f, 0);
        for (int i = 0; i < 16; i++) game.WriteMemory((ushort)(0x9000 + 64 * 16 + i), (byte)(i + 31));
        byte[] tilesBefore = Memory(game, 0x8000, 0x1800);
        byte[] palettesBefore = Palettes(game);
        game.WriteMemory(0xff40, lcd);
        game.Tap(Button.A, 3);
        game.RunFrames(3);
        game.WriteMemory(0xff40, (byte)(lcd & 0x7f));
        Assert.Equal(3, game.ReadSprite(39, GameBoyTest.SpriteByte.Tile));
        Assert.Equal(tilesBefore, Memory(game, 0x8000, 0x1800));
        Assert.Equal(palettesBefore, Palettes(game));
        Assert.Equal(map[1] + 64, game.ReadMemory(0x9bff));
        Assert.Equal(map[2] + 64, game.ReadMemory(0x9be0));
        Assert.Equal(map[2] + 64, game.ReadMemory(0x9be2));
        Assert.Equal(17, game.ReadMemory(0x9be3));
        Assert.Equal(map[1] + 100, game.ReadMemory(0x9fff));
        Assert.Equal(map[2] + 100, game.ReadMemory(0x9fe0));
        if (attrs is not null)
        {
            Assert.True(attrs.Any(a => (a & 0x60) != 0), "fixture must carry deduplicated flip flags");
            game.WriteMemory(0xff4f, 1);
            Assert.Equal((attrs[1] & 0xf8) | ((attrs[1] & 7) + 2), game.ReadMemory(0x9bff));
            Assert.Equal((attrs[1] & 0xf8) | ((attrs[1] & 7) + 5), game.ReadMemory(0x9fff));
        }
    }

    [Theory]
    [InlineData("Move", 0)]
    [InlineData("MoveFlippedX", 0x20)]
    [InlineData("MoveFlippedY", 0x40)]
    [InlineData("MoveFlippedXy", 0x60)]
    public void MetaspritePalettePlacementPreservesEveryFlipVariant(string method, int flip)
    {
        if (!TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable) return;
        string source = $$"""
            using GB;
            using static GB.Hardware;
            public static class Program {
                [Bank(2), Metasprite("art.png", FrameWidth = 1, FrameHeight = 1)] static MetaspriteAsset Hero;
                public static void Main() {
                    Display.Disable();
                    Metasprites.Load(Hero, 192, 4);
                    Metasprites.{{method}}(Hero, 0, 192, 8, 80, 80, 4);
                    Display.Enable();
                    while (true) Game.WaitVBlank();
                }
            }
            """;
        var images = new Dictionary<string, byte[]> { ["art.png"] = TestPng.Rgb(8, 8, (x, _) => x < 4 ? new Rgba32(255, 255, 255, 255) : new Rgba32(85, 85, 85, 255)) };
        var build = TestHarness.BuildRomWithAssets(source, images, GBTarget.GameBoyColor);
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        using var game = GameBoyTest.Load(build.RomPath!);
        game.RunFrames(15);
        Assert.Equal(192, game.ReadSprite(8, GameBoyTest.SpriteByte.Tile));
        Assert.Equal(flip | 4, game.ReadSprite(8, GameBoyTest.SpriteByte.Flags));
    }

    private static Dictionary<string, byte[]> Images() => new()
    {
        ["art.png"] = TestPng.Rgb(32, 8, (x, y) =>
        {
            int localX = x % 8;
            if (x / 8 == 1) localX = 7 - localX;
            return x / 8 == 2 || localX < y / 2 ? new Rgba32(0, 0, 0, 255) : new Rgba32(170, 170, 170, 255);
        }),
    };

    private static byte[] Blob(IRModule module, string suffix) =>
        ((IRDataBlob)module.Globals.Single(g => g.Name.EndsWith(suffix, StringComparison.Ordinal)).Initializer!).Bytes.ToArray();

    private static byte[] Memory(GameBoyTest game, int address, int length) =>
        Enumerable.Range(address, length).Select(a => game.ReadMemory((ushort)a)).ToArray();

    private static byte[] Palettes(GameBoyTest game) => Enumerable.Range(0, 64).Select(i =>
    {
        game.WriteMemory(0xff68, (byte)i);
        return game.ReadMemory(0xff69);
    }).ToArray();
}

