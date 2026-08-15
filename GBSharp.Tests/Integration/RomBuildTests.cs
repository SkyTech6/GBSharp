using System.Diagnostics;
using System.Text.Json;
using GBSharp.Assets.Images;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Backend.GBDK.Toolchain;
using GBSharp.Tests.Assets;

namespace GBSharp.Tests.Integration;

/// <summary>
/// The full pipeline: C# through GBDK to a ROM the boot ROM would accept.
/// </summary>
/// <remarks>
/// The only layer that needs GBDK installed. Each test skips itself when the
/// toolchain is absent so a bare checkout still runs a green suite; CI installs
/// GBDK, so these do run there.
/// </remarks>
public sealed class RomBuildTests
{
    /// <summary>
    /// xUnit v2 has no runtime skip, so an absent toolchain returns early. CI
    /// runs tools/get-gbdk.ps1 before the suite and sets
    /// <see cref="TestHarness.RequireGbdkVariable"/>, which turns a skip there
    /// into a failure: otherwise a broken toolchain lookup would make this
    /// whole file pass without running.
    /// </summary>
    private static bool SkipWithoutGbdk() => !TestHarness.GbdkAvailable;

    [Fact]
    public void MinimalProgramProducesABootableRom()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("""
                    Display.Enable();

                    byte x = 80;

                    while (true)
                    {
                        if (Input.Right)
                            x++;

                        Sprites[0].X = x;

                        Game.WaitVBlank();
                    }
            """));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.NotNull(header);

        // These three together are what "this ROM boots" means without an
        // emulator: the boot ROM compares the logo and the header checksum.
        Assert.True(header!.HasValidLogo, "the Nintendo logo must be intact");
        Assert.Equal(header.ComputedHeaderChecksum, header.StoredHeaderChecksum);
        Assert.Equal(header.ComputedGlobalChecksum, header.StoredGlobalChecksum);
        Assert.True(header.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void RomCarriesTheProjectNameAsItsCartridgeTitle()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(
            TestHarness.Program("        Display.Enable();"),
            moduleName: "Widget");

        RomHeader? header = RomHeader.Read(build.RomPath!);

        Assert.NotNull(header);
        Assert.Equal("WIDGET", header!.Title);
    }

    [Fact]
    public void ColorTargetSetsTheCgbFlag()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(
            TestHarness.Program("        Display.Enable();"),
            target: GBTarget.GameBoyColor);

        RomHeader? header = RomHeader.Read(build.RomPath!);

        Assert.NotNull(header);
        Assert.True(header!.IsColorEnabled, "a gbc build must mark the cartridge as Color compatible");
        Assert.True(header.IsValid);
    }

    [Fact]
    public void LanguageCoreCompilesThroughGbdk()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        // Structs, enums, fixed collections, arrays, ref parameters, for and
        // switch all have to survive SDCC, not just the emitter.
        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(
            """
                    Display.Enable();

                    Enemy spawned = new Enemy();
                    spawned.X = 40;
                    spawned.Kind = Kind.Walker;
                    State.Enemies.Add(spawned);

                    for (byte i = 0; i < State.Enemies.Count; i++)
                    {
                        Systems.Update(ref State.Enemies[i]);
                        Sprites.Move(i, State.Enemies[i].X, State.Enemies[i].Y);
                    }

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
            """,
            """
            public enum Kind : byte { Walker = 0, Flyer = 1 }

            public struct Enemy
            {
                public byte X;
                public byte Y;
                public Kind Kind;
            }

            public static class State
            {
                [Capacity(8)]
                public static FixedList<Enemy> Enemies;

                public static byte[] Lanes = new byte[4];
            }

            public static class Systems
            {
                public static void Update(ref Enemy enemy)
                {
                    switch (enemy.Kind)
                    {
                        case Kind.Walker:
                            enemy.X++;
                            break;
                        case Kind.Flyer:
                            enemy.Y = (byte)(64 + ((enemy.X >> 3) & 7));
                            break;
                    }
                }
            }
            """));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.NotNull(header);
        Assert.True(header!.IsValid, $"header should be valid: {header}");
    }

    /// <summary>
    /// Instance members, through the toolchain rather than only the emitter.
    /// </summary>
    /// <remarks>
    /// Struct methods, struct properties, user properties and constructors all
    /// emitted C that SDCC rejected while the emission tests were green: a
    /// snapshot assertion matches a call whose argument is the wrong shape, and
    /// only a compiler objects. So this exists specifically to put a C compiler
    /// behind those four, and the assertion is that the ROM builds at all.
    /// </remarks>
    [Fact]
    public void InstanceMembersCompileThroughGbdk()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(
            """
                    Display.Enable();

                    Point p = new Point(3, 4);
                    p.Bump();

                    Origin.Value = new Point(10, 20);
                    Origin.Value.Bump();

                    Counters.Value = p.Sum();
                    Counters.Value += Origin.Value.Doubled;
                    Counters.Value++;

                    Helpers.ByRef(ref Origin.Value);

                    Sprites.Move(0, Counters.Value, p.Y);

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
            """,
            """
            public struct Point
            {
                public byte X;
                public byte Y;

                public Point(byte x, byte y)
                {
                    X = x;
                    Y = y;
                }

                public byte Sum() => (byte)(X + Y);

                public byte Doubled => (byte)(X + X);

                public void Bump()
                {
                    X++;
                }
            }

            public static class Origin
            {
                public static Point Value;
            }

            public static class Counters
            {
                private static byte storage;

                public static byte Value
                {
                    get { return storage; }
                    set { storage = value; }
                }
            }

            public static class Helpers
            {
                public static void ByRef(ref Point p)
                {
                    p.Bump();
                }
            }
            """));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.NotNull(header);
        Assert.True(header!.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void GeneratedCIsKeptWhenAsked()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("        Display.Enable();"));

        Assert.NotNull(build.GeneratedCDirectory);
        Assert.True(Directory.Exists(build.GeneratedCDirectory), "generated C should be on disk for inspection");

        foreach (EmittedFile file in build.GeneratedFiles)
        {
            string path = Path.Combine(build.GeneratedCDirectory!, file.Name);
            Assert.True(File.Exists(path), $"{file.Name} should have been written");
        }
    }

    [Fact]
    public void AnnotateSourceWritesASourceMapBesideTheGeneratedC()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(
            TestHarness.Program("        Display.Enable();"),
            annotateSource: true);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        Assert.NotNull(build.GeneratedCDirectory);

        string sourceMapPath = Path.Combine(build.GeneratedCDirectory!, "sourcemap.json");
        Assert.True(File.Exists(sourceMapPath), "sourcemap.json should be written next to the generated C");

        // Deserialised with the reflection-based overload rather than
        // SourceMapJson: that source-gen context is internal to
        // GBSharp.Backend.GBDK, and sourcemap.json is meant to be read by any
        // tool, not only one that can see GB#'s own serializer context.
        SourceMapEntry[]? entries = JsonSerializer.Deserialize<SourceMapEntry[]>(
            File.ReadAllText(sourceMapPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.GeneratedFile == "game.c" && e.File.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceMapIsNotWrittenWithoutAnnotateSource()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("        Display.Enable();"));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        Assert.NotNull(build.GeneratedCDirectory);

        string sourceMapPath = Path.Combine(build.GeneratedCDirectory!, "sourcemap.json");
        Assert.False(File.Exists(sourceMapPath), "sourcemap.json should only appear under --annotate-source");
    }

    /// <summary>Enough tile, map and palette data for the framework to load.</summary>
    private const string ArtData = """
        public static class Art
        {
            public static readonly byte[] Tiles =
            {
                0x00, 0x18, 0x24, 0x42, 0x42, 0x24, 0x18, 0x00,
                0x00, 0x18, 0x24, 0x42, 0x42, 0x24, 0x18, 0x00,
            };

            public static readonly byte[] Map = { 0, 0, 0, 0 };

            public static readonly ushort[] Colors = { 0x7FFF, 0x35AD, 0x1A73, 0x0000 };
        }
        """;

    [Fact]
    public void BackgroundsAndShadesCompileThroughGbdk()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(
            """
                    Display.Disable();
                    Background.LoadTiles(0, 1, Art.Tiles);
                    Background.LoadMap(0, 0, 2, 2, Art.Map);
                    Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
                    Display.Enable();
                    Display.ShowBackground();
                    Background.Move(1, 1);
            """,
            ArtData));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
    }

    [Fact]
    public void ColorPalettesCompileThroughGbdk()
    {
        // ushort[] has to reach GBDK's 'const palette_color_t*' intact, and the
        // colour palette entry points live in cgb.h, which gb.h does not
        // include. Both are things only a real compile can confirm.
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(
            TestHarness.Program(
                """
                        if (Palettes.IsColorHardware)
                        {
                            Palettes.LoadBackgroundColors(0, 1, Art.Colors);
                            Palettes.LoadSpriteColors(0, 1, Art.Colors);
                            BackgroundPalettes[1].SetColor(0, Palettes.Rgb(31, 0, 0));
                        }
                """,
                ArtData),
            target: GBTarget.GameBoyColor);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsColorEnabled, "the ROM should be marked Game Boy Color");
    }

    [Fact]
    public void AudioCompilesThroughGbdk()
    {
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("""
                    Audio.Enable();
                    Audio.SetMasterVolume(7, 7);
                    Audio.PlayTone(Channel.Pulse1, Note.A4, 12, Duty.Half);
                    Audio.PlayNoise(8, 0x33);
                    Audio.Stop(Channel.Pulse1);
                    Audio.Disable();
            """));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
    }

    [Fact]
    public void AnImageBecomesAWorkingRom()
    {
        // The whole point of the phase: a PNG on disk, decoded, converted,
        // placed in ROM and linked, with no step the developer had to run.
        if (SkipWithoutGbdk())
        {
            return;
        }

        byte[] png = TestPng.Rgb(32, 32, (x, y) => (((x / 4) + (y / 4)) % 4) switch
        {
            0 => new Rgba32(255, 255, 255, 255),
            1 => new Rgba32(170, 170, 170, 255),
            2 => new Rgba32(85, 85, 85, 255),
            _ => new Rgba32(0, 0, 0, 255),
        });

        RomBuildResult build = TestHarness.BuildRomWithAssets(
            """
            using GB;

            public static class Program
            {
                [Asset("art.png")]
                private static TileMap Art;

                public static void Main()
                {
                    Display.Disable();
                    Background.Load(Art);
                    Display.Enable();
                    Display.ShowBackground();
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = png });

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void AnImageOnTheWindowLayerBecomesAWorkingRom()
    {
        // Window.Load and Window.LoadAttributes have no GBDK counterpart to
        // call directly (gb.h only has set_bkg_attributes) - gbs_runtime.c
        // does the VBK_REG dance with set_win_tiles by hand, so this is the
        // one check that it actually links and runs against real gb/gb.h.
        if (SkipWithoutGbdk())
        {
            return;
        }

        byte[] png = TestPng.Rgb(32, 32, (x, y) => (((x / 4) + (y / 4)) % 4) switch
        {
            0 => new Rgba32(255, 255, 255, 255),
            1 => new Rgba32(170, 170, 170, 255),
            2 => new Rgba32(85, 85, 85, 255),
            _ => new Rgba32(0, 0, 0, 255),
        });

        RomBuildResult build = TestHarness.BuildRomWithAssets(
            """
            using GB;

            public static class Program
            {
                [Asset("art.png")]
                private static TileMap Art;

                public static void Main()
                {
                    Display.Disable();
                    Window.Load(Art);
                    Window.Move(Window.MinX, 0);
                    Display.Enable();
                    Display.ShowWindow();
                }
            }
            """,
            new Dictionary<string, byte[]> { ["art.png"] = png });

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void AFontBecomesAWorkingRom()
    {
        // gbs_font_draw indexes glyph_table with a raw byte from text and hands
        // the result straight to set_bkg_tile_xy - the one part of this feature
        // no IR-level test can check, because it depends on SDCC agreeing with
        // what gbs_runtime.c wrote against the real gb/gb.h.
        if (SkipWithoutGbdk())
        {
            return;
        }

        byte[] png = TestPng.Rgb(16, 8, (x, _) => x < 8
            ? new Rgba32(255, 255, 255, 255)
            : new Rgba32(0, 0, 0, 255));

        RomBuildResult build = TestHarness.BuildRomWithAssets(
            """
            using GB;

            public static class Program
            {
                [Font("alphabet.png", Characters = "AB")]
                private static FontAsset Alphabet;

                private static readonly byte[] Label = { 65, 66, 65, 66 };

                public static void Main()
                {
                    Display.Disable();
                    Text.Load(Alphabet, 0);
                    Display.Enable();
                    Display.ShowBackground();

                    Text.Draw(Alphabet, 0, 0, 0, 4, Label);

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
                }
            }
            """,
            new Dictionary<string, byte[]> { ["alphabet.png"] = png });

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void TextDrawWindowBecomesAWorkingRom()
    {
        // gbs_win_font_draw is gbs_font_draw's set_win_tile_xy twin - the same
        // real-GBDK-linking gap Text.Draw itself covers, just for the window.
        if (SkipWithoutGbdk())
        {
            return;
        }

        byte[] png = TestPng.Rgb(16, 8, (x, _) => x < 8
            ? new Rgba32(255, 255, 255, 255)
            : new Rgba32(0, 0, 0, 255));

        RomBuildResult build = TestHarness.BuildRomWithAssets(
            """
            using GB;

            public static class Program
            {
                [Font("alphabet.png", Characters = "AB")]
                private static FontAsset Alphabet;

                private static readonly byte[] Label = { 65, 66, 65, 66 };

                public static void Main()
                {
                    Display.Disable();
                    Text.Load(Alphabet, 0);
                    Display.Enable();
                    Display.ShowWindow();

                    Text.DrawWindow(Alphabet, 0, 0, 0, 4, Label);

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
                }
            }
            """,
            new Dictionary<string, byte[]> { ["alphabet.png"] = png });

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void AMetaspriteBecomesAWorkingRom()
    {
        // gbs_metasprite_move casts the frame blob to a real metasprite_t* and
        // hands it to GBDK's move_metasprite_ex - the one part of this feature
        // no IR-level test can check, because it depends on SDCC and the real
        // gb/metasprites.h agreeing with what gbs_runtime.c wrote against them.
        if (SkipWithoutGbdk())
        {
            return;
        }

        byte[] png = TestPng.Rgb(16, 16, (x, y) =>
        {
            int tile = (x / 8) + ((y / 8) * 2);
            return tile == 3
                ? new Rgba32(255, 255, 255, 255) // bottom-right sub-sprite: blank
                : new Rgba32(85, 85, 85, 255);
        });

        RomBuildResult build = TestHarness.BuildRomWithAssets(
            """
            using GB;
            using static GB.Hardware;

            public static class Program
            {
                [Metasprite("hero.png", FrameWidth = 2, FrameHeight = 2)]
                private static MetaspriteAsset Hero;

                public static void Main()
                {
                    Display.Enable();
                    Display.ShowSprites();

                    Metasprites.Load(Hero);

                    byte used = Metasprites.Move(Hero, 0, 0, 0, 80, 80);
                    Metasprites.HideRange(used, 40);

                    while (true)
                    {
                        Game.WaitVBlank();
                    }
                }
            }
            """,
            new Dictionary<string, byte[]> { ["hero.png"] = png });

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void BankUsageIsReadFromTheLinkerMap()
    {
        // The resource report is only worth printing if it says what the linker
        // actually placed rather than what GB# hoped it would.
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("        Display.Enable();"));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        Assert.NotNull(build.Usage);

        BankUsage bank0 = Assert.Single(build.Usage!.Rom, b => b.BankNumber == 0);
        Assert.Equal(16384, bank0.Size);
        Assert.InRange(bank0.Used, 1, 16384);

        // WRAM is larger than anything the program declared, because the stack,
        // shadow OAM and the GBDK library live there too.
        Assert.True(build.Usage.WramUsed > 0, "WRAM usage should be reported");
    }

    [Fact]
    public void MultipleTranslationUnitsLink()
    {
        // Every unit includes the runtime shim, so the shim's functions have to
        // survive being seen more than once. This is the test that would catch
        // the shim regressing to a linkage that only works for a single file.
        if (SkipWithoutGbdk())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("        Display.Enable();"));

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));
        Assert.Contains(build.GeneratedFiles, f => f.Kind == EmittedFileKind.Header);
        Assert.Contains(build.GeneratedFiles, f => f.Kind == EmittedFileKind.TranslationUnit);
    }

    [Fact]
    public void AnExternalLibraryLinksIntoAWorkingRom()
    {
        // GB# does not own a music engine (Audio.cs says so explicitly) and has
        // no bindings for hUGEDriver or anything like it. What it does need is
        // the ability to link an external object/library a developer supplies,
        // the same way any C toolchain does. This is that: a trivial object,
        // compiled with the vendored SDCC ourselves so the test has no
        // dependency on a real music driver, declared via "libraries" and
        // linked in. Nothing calls into it - GB# has no way to, and does not
        // need one - this only proves the link step itself works.
        if (SkipWithoutGbdk())
        {
            return;
        }

        string libraryPath = CompileFixtureLibrary();

        var module = TestHarness.CompileModule(TestHarness.Program("        Display.Enable();"));

        string outputDirectory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"), "build");
        var options = new RomBuildOptions(outputDirectory, KeepGeneratedC: true)
        {
            Libraries = [libraryPath],
        };

        RomBuildResult build = new RomBuilder().Build(module, options);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        // The library must have been copied beside the generated C - the
        // invariant this whole file is built on is that only a bare file name,
        // never a project-directory path, reaches lcc's command line.
        Assert.True(
            File.Exists(Path.Combine(build.GeneratedCDirectory!, Path.GetFileName(libraryPath))),
            "the library should have been copied beside the generated C");

        RomHeader? header = RomHeader.Read(build.RomPath!);
        Assert.True(header?.IsValid, $"header should be valid: {header}");
    }

    [Fact]
    public void ANativeCallIntoAUserLibraryBuildsWithAnIncludedHeader()
    {
        // The whole escape-hatch story in one build: a [Native] method mapped
        // to a function the framework does not know, defined in a C file the
        // project supplies under "libraries", declared by a header supplied
        // under "includes". Without the include, SDCC's implicit-declaration
        // handling fails the build ("too many parameters"), which is exactly
        // the failure this feature exists to prevent.
        if (SkipWithoutGbdk())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", "native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string sourcePath = Path.Combine(directory, "gbs_test_native.c");
        string headerPath = Path.Combine(directory, "gbs_test_native.h");

        File.WriteAllText(sourcePath, """
            #include <stdint.h>
            #include "gbs_test_native.h"

            uint8_t gbs_test_native_echo(uint8_t value)
            {
                return value;
            }
            """);

        File.WriteAllText(headerPath, """
            #ifndef GBS_TEST_NATIVE_H
            #define GBS_TEST_NATIVE_H
            #include <stdint.h>
            uint8_t gbs_test_native_echo(uint8_t value);
            #endif
            """);

        var module = TestHarness.CompileModule(TestHarness.Program(
            "        Raw.Echoed = Raw.Echo(42);",
            """
            public static class Raw
            {
                public static byte Echoed;

                [Native("gbs_test_native_echo")]
                public static byte Echo(byte value) => throw new System.NotSupportedException();
            }
            """));

        string outputDirectory = Path.Combine(directory, "build");
        var options = new RomBuildOptions(outputDirectory, KeepGeneratedC: true)
        {
            Libraries = [sourcePath],
            Includes = [headerPath],
        };

        RomBuildResult build = new RomBuilder().Build(module, options);

        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        // The header must have been copied beside the generated C, where the
        // emitted #include "gbs_test_native.h" resolves.
        Assert.True(
            File.Exists(Path.Combine(build.GeneratedCDirectory!, "gbs_test_native.h")),
            "the include should have been copied beside the generated C");

        string header = File.ReadAllText(Path.Combine(build.GeneratedCDirectory!, CEmitter.HeaderFileName));
        Assert.Contains("#include \"gbs_test_native.h\"", header);

        RomHeader? romHeader = RomHeader.Read(build.RomPath!);
        Assert.True(romHeader?.IsValid, $"header should be valid: {romHeader}");
    }

    /// <summary>
    /// Compiles a trivial, dependency-free function into a real SDCC object
    /// with the vendored toolchain, standing in for a prebuilt library like
    /// hUGEDriver without checking a binary into the repository or coupling
    /// the test to one exact SDCC build.
    /// </summary>
    private static string CompileFixtureLibrary()
    {
        bool located = GbdkToolchain.TryLocate(null, out GbdkToolchain? toolchain, out _);
        Assert.True(located && toolchain is not null, "GBDK should be available - this is only reached when SkipWithoutGbdk() is false");

        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        const string sourceName = "gbs_test_fixture.c";
        const string libraryName = "gbs_test_fixture.rel";

        File.WriteAllText(Path.Combine(directory, sourceName), """
            void gbs_test_fixture_symbol(void)
            {
            }
            """);

        var startInfo = new ProcessStartInfo(toolchain!.CompilerDriver)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(libraryName);
        startInfo.ArgumentList.Add(sourceName);

        using Process process = Process.Start(startInfo)!;
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string libraryPath = Path.Combine(directory, libraryName);

        Assert.True(
            process.ExitCode == 0 && File.Exists(libraryPath),
            $"fixture library failed to compile (exit {process.ExitCode}):\n{standardOutput}\n{standardError}");

        return libraryPath;
    }
}
