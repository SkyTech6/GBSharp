using System.Diagnostics;
using GBSharp.Backend.GBDK;
using GBSharp.Cli;
using GBSharp.Cli.Publishing;

namespace GBSharp.Tests.Integration;

/// <summary>
/// A published game, started the way a person who was given one would start it.
/// </summary>
/// <remarks>
/// <para>
/// The layer above <see cref="EmulatedRomTests"/>: not "does the ROM run in the
/// emulator" but "does the thing we hand to a player run". Those are different
/// claims, and the second one involves a stub built on another machine, a
/// payload format written in C# and read in C, and a window system.
/// </para>
/// <para>
/// The Player runs against SDL's dummy video and audio drivers here, so this
/// needs no display and works in CI. What it cannot check is that the picture
/// reached a real screen; what it can check, and does, is that the pixels the
/// Player was about to draw are exactly the pixels the emulator produced.
/// </para>
/// </remarks>
public sealed class PublishedGameTests
{
    private static string? PlayerPath => PlayerStub.Installed();

    private static bool SkipWithoutPlayer() =>
        !TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable || PlayerPath is null;

    private const string SpriteProgram = """
                Display.Enable();

                byte x = 80;

                while (true)
                {
                    if (Input.Right)
                        x++;

                    Sprites[0].X = x;

                    Game.WaitVBlank();
                }
        """;

    private static string PublishToTemporary(string romPath, string config)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-published", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string game = Path.Combine(directory, "Game" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

        GamePayload.Write(PlayerPath!, game, File.ReadAllBytes(romPath), config);

        // Whatever the stub needs beside it, which on Windows is SDL2.dll.
        foreach (string companion in PlayerStub.RuntimeCompanions(PlayerPath!))
        {
            File.Copy(companion, Path.Combine(directory, Path.GetFileName(companion)), overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                game,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return game;
    }

    private static (int ExitCode, string Output) Run(string game, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(game)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(game),
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // No display and no sound card needed, which is what lets this run in CI.
        startInfo.Environment["SDL_VIDEODRIVER"] = "dummy";
        startInfo.Environment["SDL_AUDIODRIVER"] = "dummy";

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(60_000), "the published game did not exit within a minute");

        return (process.ExitCode, output);
    }

    [Fact]
    public void APublishedGameStartsAndDrawsWhatTheEmulatorProduced()
    {
        if (SkipWithoutPlayer())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteProgram));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        string game = PublishToTemporary(build.RomPath!, PlayerSettings.Serialize(null, "Test Game"));
        string screenshot = Path.Combine(Path.GetDirectoryName(game)!, "screen.bmp");

        const int frames = 60;
        (int exitCode, string output) = Run(game, "--frames", frames.ToString(), "--screenshot", screenshot);

        Assert.True(exitCode == 0, $"the published game exited with {exitCode}: {output}");
        Assert.True(File.Exists(screenshot), "the published game drew no frames");

        // The claim worth making is not that it drew something, but that it drew
        // the right thing. The emulator is asked for the same frame separately,
        // and the two have to agree pixel for pixel.
        using GameBoyTest reference = GameBoyTest.Load(build.RomPath!);
        reference.RunFrames(frames);

        Assert.Equal(reference.Screen.ToArray(), ReadBitmap(screenshot));
    }

    [Fact]
    public void APublishedGameCarriesItsRomRatherThanReadingOne()
    {
        if (SkipWithoutPlayer())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteProgram));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        string game = PublishToTemporary(build.RomPath!, PlayerSettings.Serialize(null, "Test Game"));

        // Deleting the ROM the game was published from must change nothing: if
        // it did, publishing would have produced a shortcut rather than a game.
        File.Delete(build.RomPath!);

        (int exitCode, string output) = Run(game, "--frames", "10");

        Assert.True(exitCode == 0, $"the published game exited with {exitCode}: {output}");
    }

    [Fact]
    public void ADamagedGameSaysSoRatherThanStarting()
    {
        if (SkipWithoutPlayer())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteProgram));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        string game = PublishToTemporary(build.RomPath!, PlayerSettings.Serialize(null, "Test Game"));

        // One byte, in the middle of the ROM, as a bad download would leave it.
        byte[] bytes = File.ReadAllBytes(game);
        bytes[bytes.Length - GamePayload.TrailerSize - 100] ^= 0xFF;
        File.WriteAllBytes(game, bytes);

        (int exitCode, string output) = Run(game, "--frames", "10");

        Assert.True(exitCode != 0, "a damaged game should refuse to start");
        Assert.Contains("damaged", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a 32bpp bottom-up BMP back into the ABI's pixel order.</summary>
    private static uint[] ReadBitmap(string path)
    {
        byte[] data = File.ReadAllBytes(path);

        int offset = BitConverter.ToInt32(data, 10);
        int width = BitConverter.ToInt32(data, 18);
        int height = BitConverter.ToInt32(data, 22);

        bool bottomUp = height > 0;
        height = Math.Abs(height);

        uint[] pixels = new uint[width * height];

        for (int y = 0; y < height; y++)
        {
            int source = bottomUp ? height - 1 - y : y;
            int row = offset + (source * width * 4);

            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = BitConverter.ToUInt32(data, row + (x * 4));
            }
        }

        return pixels;
    }
}
