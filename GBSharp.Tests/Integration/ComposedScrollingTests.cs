using GB;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Emulator;
using Xunit.Abstractions;

namespace GBSharp.Tests.Integration;

[Collection(EmulatorStateCollection.Name)]
public sealed class ComposedScrollingTests(ITestOutputHelper output)
{
    private static string Sample => Path.Combine(TestHarness.RepositoryRoot(), "Samples", "ComposedScrolling");
    private static Dictionary<string, byte[]> Images() => Directory.GetFiles(Path.Combine(Sample, "Assets"), "*.png")
        .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

    [Theory]
    [InlineData(GBTarget.GameBoy)]
    [InlineData(GBTarget.GameBoyColor)]
    public void ScrollingPreservesHudAndIndependentCharacters(GBTarget target)
    {
        if (!TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable) return;
        var build = TestHarness.BuildRomWithAssets(File.ReadAllText(Path.Combine(Sample, "Program.cs")), Images(), target);
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        using var game = GameBoyTest.Load(build.RomPath!);
        game.RunFrames(60);
        uint[] before = game.Screen.ToArray();
        byte[] graphics = Read(game, 0x8800, 2048);
        byte[] sprites = Read(game, 0xfe00, 32);
        Assert.Equal(192, sprites[2]);
        Assert.Equal(196, sprites[18]);
        Assert.True(before.Distinct().Count() >= 4, "Scene must render multiple shades/colours.");
        game.Tap(Button.Right, 24);
        game.RunFrames(2);
        Assert.InRange(game.ReadMemory(0xff43), (byte)8, (byte)96);
        Assert.Equal(before[(128 * 160)..], game.Screen[(128 * 160)..].ToArray());
        Assert.False(before[..(128 * 160)].SequenceEqual(game.Screen[..(128 * 160)].ToArray()));
        Assert.Equal(graphics, Read(game, 0x8800, 2048));
        Assert.Equal(sprites, Read(game, 0xfe00, 32));
        game.Tap(Button.Down, 24);
        game.RunFrames(2);
        Assert.InRange(game.ReadMemory(0xff42), (byte)8, (byte)112);
        Assert.Equal(before[(128 * 160)..], game.Screen[(128 * 160)..].ToArray());
        Assert.Equal(sprites, Read(game, 0xfe00, 32));
    }

    private static byte[] Read(GameBoyTest game, ushort start, int length) =>
        Enumerable.Range(start, length).Select(a => game.ReadMemory((ushort)a)).ToArray();

    [Fact]
    public void MapOnlyColumnsCostLessThanRepeatedGraphicsUploads()
    {
        if (!TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable) return;
        long oldTicks = Measure(false);
        long mapTicks = Measure(true);
        output.WriteLine($"20 exposed 1x18 columns, LCD disabled, CGB: DrawRegion={oldTicks} ticks; CopyRegion={mapTicks} ticks; reduction={100.0 * (oldTicks - mapTicks) / oldTicks:F1}%");
        Assert.True(mapTicks < oldTicks, $"Map-only {mapTicks} ticks should improve on upload path {oldTicks} ticks.");
    }

    private static long Measure(bool mapOnly)
    {
        string update = mapOnly
            ? "Background.CopyRegion(Map, x, 0, 1, 18, x, 0, 0, 0);"
            : "Background.DrawRegion(Map, x, 0, 1, 18, x, 0);";
        string source = $$"""
            using GB;
            public static class Program
            {
                [Asset("world.png")] private static TileMap Map;
                public static void Main()
                {
                    Display.Disable();
                    Background.LoadGraphics(Map, 0, 0);
                    Work();
                    End();
                }
                public static void Work()
                {
                    for (byte x = 0; x < 20; x++) { {{update}} }
                }
                public static void End() { while (true) { Game.WaitVBlank(); } }
            }
            """;
        var build = TestHarness.BuildRomWithAssets(source, Images(), GBTarget.GameBoyColor);
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        using var game = GameBoyTest.Load(build.RomPath!);
        var symbols = RomSymbolResolver.ForRom(build.RomPath!).Extents().ToArray();
        var begin = symbols.Single(e => e.Method == "Program.Work()");
        var end = symbols.Single(e => e.Method == "Program.End()");
        try
        {
            game.Machine.AddBreakpoint(begin.Bank, begin.Start);
            Assert.True(game.Machine.RunFrames(60).HasFlag(EmulatorEvent.Breakpoint));
            GameBoy.ClearBreakpoints();
            GameBoy.ClearProfile();
            Assert.True(GameBoy.SetProfilingEnabled(true));
            game.Machine.AddBreakpoint(end.Bank, end.Start);
            Assert.True(game.Machine.RunFrames(120).HasFlag(EmulatorEvent.Breakpoint));
            var cycles = new uint[game.Machine.RomSize];
            game.Machine.ReadProfile([], cycles);
            return cycles.Sum(c => (long)c);
        }
        finally
        {
            GameBoy.SetProfilingEnabled(false);
            GameBoy.ClearProfile();
            GameBoy.ClearBreakpoints();
        }
    }
}
