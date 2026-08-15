namespace GBSharp.Tests.Framework;

/// <summary>
/// The framework surface as a whole.
/// </summary>
/// <remarks>
/// The compiler knows nothing about any individual framework member: they are
/// ordinary declarations carrying <c>[Native]</c> or <c>[NativeIdentity]</c>.
/// That is the architecture working, but it also means a mistyped attribute or
/// a member that cannot lower is invisible until someone writes a game using
/// it. This file is the cheapest guard against that: it uses everything.
/// </remarks>
public sealed class FrameworkSurfaceTests
{
    /// <summary>Touches every framework member that exists.</summary>
    private const string UsesEverything = """
                byte b = 3;
                bool flag;

                Display.Enable();
                Display.Disable();
                Display.ShowSprites();
                Display.HideSprites();
                Display.ShowBackground();
                Display.HideBackground();
                Display.ShowWindow();
                Display.HideWindow();
                Display.UseTallSprites();
                Display.UseShortSprites();

                flag = Input.Right || Input.Left || Input.Up || Input.Down;
                flag = Input.A || Input.B || Input.Start || Input.Select;
                b = Input.Read();

                Background.LoadTiles(0, 1, Art.Tiles);
                Background.LoadMap(0, 0, 2, 2, Art.Map);
                Background.LoadAttributes(0, 0, 2, 2, Art.Map);
                Background.SetTile(1, 1, 2);
                b = Background.GetTile(1, 1);
                Background.Move(4, 4);
                Background.Scroll(-1, 1);
                Background.ScrollX = 5;
                b = Background.ScrollY;

                Window.LoadMap(0, 0, 2, 2, Art.Map);
                Window.LoadAttributes(0, 0, 2, 2, Art.Map);
                Window.SetTile(0, 0, 1);
                b = Window.GetTile(0, 0);
                Window.Move(Window.MinX, 100);
                Window.Scroll(1, -1);
                Window.X = 7;
                b = Window.Y;

                Tiles.LoadBackground(0, 1, Art.Tiles);
                Tiles.LoadWindow(0, 1, Art.Tiles);
                Tiles.LoadSprite(0, 1, Art.Tiles);

                Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
                Palettes.SetSpriteShades(0, Shade.White, Shade.White, Shade.DarkGray, Shade.Black);
                Palettes.BackgroundRaw = 0xE4;
                b = Palettes.BackgroundRaw;
                flag = Palettes.IsColorHardware;
                Palettes.LoadBackgroundColors(0, 1, Art.Colors);
                Palettes.LoadSpriteColors(0, 1, Art.Colors);
                Palettes.UseDefaultColors();
                BackgroundPalettes[1].SetColor(0, Palettes.Rgb(31, 0, 0));
                SpritePalettes[2].SetColor(3, 0);

                Sprites.Move(0, 80, 72);
                Sprites.SetTile(0, 1);
                Sprites.Hide(1);
                Sprites.HideAll();
                Sprites.Scroll(0, 1, -1);
                Sprites.SetFlags(0, SpriteFlags.FlipX | SpriteFlags.FlipY);
                Sprites.LoadTiles(0, 1, Art.Tiles);
                Sprites[0].X = 40;
                Sprites[0].Y = 40;
                Sprites[0].Tile = 2;
                Sprites[0].Flags = SpriteFlags.None;
                Sprites[0].FlipX = true;
                Sprites[0].FlipY = false;
                Sprites[0].BehindBackground = true;
                Sprites[0].Palette = 3;
                Sprites[0].UseSecondPalette = true;
                b = Sprites[0].X;

                Audio.Enable();
                Audio.SetMasterVolume(7, 7);
                Audio.SetRouting(0xFF);
                Audio.PlayTone(Channel.Pulse2, Note.C4, 15, Duty.Quarter);
                Audio.PlayNoise(10, 0x33);
                Audio.Stop(Channel.Pulse2);
                Audio.Disable();

                Game.WaitVBlank();
                Game.Halt();
                Sprites.Hide(b);
        """;

    private const string Data = """
        public static class Art
        {
            public static readonly byte[] Tiles =
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            };

            public static readonly byte[] Map = { 0, 1, 1, 0 };

            public static readonly ushort[] Colors = { 0x7FFF, 0x35AD, 0x1A73, 0x0000 };
        }
        """;

    [Fact]
    public void EveryFrameworkMemberLowers()
    {
        IReadOnlyList<GBSharp.Compiler.Diagnostics.GBDiagnostic> diagnostics =
            TestHarness.DiagnosticsFor(TestHarness.Program(UsesEverything, Data));

        Assert.True(
            diagnostics.All(d => d.Severity != GBSharp.Compiler.Diagnostics.GBSeverity.Error),
            "the framework should lower without errors:\n" + TestHarness.Describe(diagnostics));
    }

    [Fact]
    public void NoFrameworkTypeSurvivesIntoTheGeneratedC()
    {
        // The handle types exist to make C# syntax work and are supposed to
        // vanish. If one of these names reaches the C, something that should
        // have erased became a real value.
        string c = TestHarness.EmitC(TestHarness.Program(UsesEverything, Data));

        foreach (string name in (string[])
        [
            "SpriteTable", "SpriteRef", "BackgroundPaletteTable", "BackgroundPaletteRef",
            "SpritePaletteTable", "SpritePaletteRef", "Shade", "Channel", "Note", "Duty",
            "SpriteFlags", "Hardware",
        ])
        {
            Assert.DoesNotContain(name, c);
        }
    }

    [Fact]
    public void PaletteIndexerErasesToAPaletteNumber()
    {
        // The same trick as Sprites[0].X: the handle carries the index and
        // nothing else, so two levels of syntax become one call.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        BackgroundPalettes[2].SetColor(1, 0x7FFF);"));

        Assert.Contains("set_bkg_palette_entry(2U, 1U, 32767U);", c);
    }

    [Fact]
    public void NotesFoldToLiteralsRatherThanATable()
    {
        // The enum is ushort-backed precisely so this happens. A note table in
        // ROM would be the framework quietly spending the developer's budget.
        string c = TestHarness.EmitC(TestHarness.Program(
            "        Audio.PlayTone(Channel.Pulse1, Note.A4, 12, Duty.Half);"));

        Assert.Contains("gbs_audio_tone(1U, 1750U, 12U, 128U);", c);
    }
}
