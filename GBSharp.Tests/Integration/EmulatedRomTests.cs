using GB;
using GBSharp.Backend.GBDK;
using GBSharp.Emulator;

namespace GBSharp.Tests.Integration;

/// <summary>
/// The layer above <see cref="RomBuildTests"/>: ROMs that are not merely
/// well formed but actually run.
/// </summary>
/// <remarks>
/// <para>
/// A valid header proves GBDK linked something. It says nothing about whether
/// the code GB# generated does what the C# said, which is the only question
/// that matters about a compiler. These tests boot the ROM, press buttons and
/// read the memory the program wrote.
/// </para>
/// <para>
/// Two things can be absent: GBDK, which builds the ROM, and the emulator
/// runtime, which runs it. Each test returns early when what it needs is
/// missing, and CI sets both require variables so that skipping there is a bug
/// rather than a quiet pass.
/// </para>
/// </remarks>
public sealed class EmulatedRomTests
{
    private static bool SkipWithoutBoth() =>
        !TestHarness.GbdkAvailable || !GameBoyTest.EmulatorAvailable;

    private static bool SkipWithoutEmulator() => !GameBoyTest.EmulatorAvailable;

    /// <summary>
    /// The program from <see cref="RomBuildTests.MinimalProgramProducesABootableRom"/>,
    /// which parks a sprite at x = 80 and walks it right while the d-pad is held.
    /// </summary>
    private const string SpriteWalksRight = """
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

    [Fact]
    public void AProgramWritesWhatItSaidToOam()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteWalksRight));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        // Enough frames to clear GBDK's startup and reach the loop, which copies
        // shadow OAM into real OAM once per VBlank.
        game.RunFrames(10);

        Assert.Equal(80, game.ReadSprite(0, GameBoyTest.SpriteByte.X));
    }

    [Fact]
    public void AProgramReactsToAButtonPress()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteWalksRight));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);
        game.RunFrames(10);

        byte before = game.ReadSprite(0, GameBoyTest.SpriteByte.X);

        // The loop tests the d-pad once per frame, so holding right for N frames
        // moves the sprite by at most N. Asserting a bound rather than an exact
        // value because the press lands part way through a frame; asserting it
        // moved at all is what proves the joypad read works.
        game.Press(Button.Right);
        game.RunFrames(10);
        game.Release(Button.Right);

        byte held = game.ReadSprite(0, GameBoyTest.SpriteByte.X);

        Assert.True(
            held > before,
            $"holding right should have moved the sprite from {before}, but it is still {held}");
        Assert.True(
            held <= before + 10,
            $"ten frames cannot move the sprite more than ten pixels, but it went from {before} to {held}");

        // One frame to drain the pipeline before sampling again. What the loop
        // wrote in the frame the button came up in is still in shadow OAM, and
        // does not reach the OAM this reads until the next VBlank copies it.
        game.RunFrames(1);
        byte settled = game.ReadSprite(0, GameBoyTest.SpriteByte.X);

        // Then it stays put, which is the other half of the claim: the emulator
        // is not simply reporting every button as pressed.
        game.RunFrames(10);

        Assert.Equal(settled, game.ReadSprite(0, GameBoyTest.SpriteByte.X));
    }

    [Fact]
    public void ReleasedButtonsAreNotPressed()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteWalksRight));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        game.RunFrames(10);
        byte before = game.ReadSprite(0, GameBoyTest.SpriteByte.X);

        // A button the program does not read must not move the sprite either.
        game.Press(Button.A | Button.Start);
        game.RunFrames(20);

        Assert.Equal(before, game.ReadSprite(0, GameBoyTest.SpriteByte.X));
    }

    [Fact]
    public void ResetReturnsTheMachineToItsBootState()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program(SpriteWalksRight));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        game.RunFrames(20);
        string before = game.ScreenHash();

        // Walk the sprite somewhere else so that reset has something to undo.
        game.Press(Button.Right);
        game.RunFrames(20);
        game.Release(Button.Right);
        Assert.True(game.ReadSprite(0, GameBoyTest.SpriteByte.X) > 80);

        game.Machine.Reset();
        game.RunFrames(20);

        // Byte for byte, not approximately: an emulator whose reset is only
        // nearly deterministic is one that cannot be used for hot reload later,
        // and the same seed and the same tick count have to produce the same
        // screen.
        Assert.Equal(before, game.ScreenHash());
        Assert.Equal(80, game.ReadSprite(0, GameBoyTest.SpriteByte.X));
    }

    [Fact]
    public void ACartridgeWithoutABatteryHasNoSaveRam()
    {
        if (SkipWithoutBoth())
        {
            return;
        }

        RomBuildResult build = TestHarness.BuildRom(TestHarness.Program("        Display.Enable();"));
        Assert.True(build.Succeeded, TestHarness.Describe(build.Diagnostics));

        using GameBoyTest game = GameBoyTest.Load(build.RomPath!);

        // Not "how much RAM is on the cartridge" but "how much is worth
        // persisting". A host can therefore save unconditionally.
        Assert.Equal(0, game.Machine.SaveRamSize);
    }

    /// <summary>
    /// Blargg's CPU instruction test, run through the facade and compared to the
    /// screen hash binjgb's own test suite records for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that says the facade did not change emulation. The
    /// expected value is not something GB# chose: it is the SHA1 in
    /// <c>scripts/test.json</c> that upstream's <c>tester.c</c> produces for
    /// this ROM at this frame count, so matching it means the ABI reproduces
    /// upstream frame for frame and pixel for pixel.
    /// </para>
    /// <para>
    /// The ROM lives in the emulator submodule, which most contributors never
    /// clone, so this skips when it is not there.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("cpu_instrs.gb", 1780, "58d90d7561c7d2de728b999b8d5dd74bb6e86598")]
    [InlineData("instr_timing.gb", 42, "e84c9fce1dfba5ae7786a45db463032205dcc10c")]
    [InlineData("mem_timing.gb", 170, "560d7c80641e5ed2c0fda7309d15f8b4de47ce8d")]
    [InlineData("halt_bug.gb", 105, "51b3c49c21a7d58856fa010185f3311e61e93e41")]
    public void BlarggTestsMatchUpstreamScreenHashes(string rom, int frames, string expected)
    {
        if (SkipWithoutEmulator())
        {
            return;
        }

        string path = Path.Combine(
            TestHarness.RepositoryRoot(),
            "extern", "gbsharp-emulator", "test", "blargg", rom);

        if (!File.Exists(path))
        {
            // The submodule is not checked out. Nothing to say about it.
            return;
        }

        using GameBoyTest game = GameBoyTest.Load(path);
        game.RunFrames(frames);

        Assert.Equal(expected, game.ScreenHash());
    }

    [Fact]
    public void TheRuntimeSpeaksTheAbiThisBuildWasWrittenAgainst()
    {
        if (SkipWithoutEmulator())
        {
            return;
        }

        // Load throws with both version numbers on a mismatch, which is the
        // whole point of the check: the native library is fetched by a script and
        // this assembly is compiled from source, so they move independently.
        EmulatorRuntime.Load(EmulatorFlavour.Debug);

        Assert.Equal(EmulatorFlavour.Debug, EmulatorRuntime.LoadedFlavour);
    }

    [Fact]
    public void AskingForTheOtherFlavourInOneProcessIsRefused()
    {
        if (SkipWithoutEmulator())
        {
            return;
        }

        EmulatorRuntime.Load(EmulatorFlavour.Debug);

        // A process holds one native library. Silently handing back the debug
        // one would leave callers disagreeing about whether instrumentation
        // exists, so this is an exception rather than a shrug.
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => EmulatorRuntime.Load(EmulatorFlavour.Fast));

        Assert.Contains("already loaded", error.Message);
    }

    [Fact]
    public void BytesThatAreNotACartridgeAreRefusedWithAMessage()
    {
        if (SkipWithoutEmulator())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => GameBoyTest.Load(ReadOnlySpan<byte>.Empty));

        // 0x149 is the cartridge RAM size code, and 0x77 is not one. There is no
        // cartridge this could describe, so there is nothing to emulate.
        byte[] impossibleRam = new byte[32 * 1024];
        impossibleRam[0x149] = 0x77;

        Assert.Throws<ArgumentException>(() => GameBoyTest.Load(impossibleRam));

        // The bar is that low, and worth pinning so nobody assumes otherwise.
        // 32KB of zeroes is a ROM ONLY cartridge with no RAM as far as the
        // header is concerned, and the logo and checksums that would condemn it
        // are checks the emulator deliberately does not make. GB# makes them
        // itself, against the built ROM, in RomBuildTests.
        using GameBoyTest zeroed = GameBoyTest.Load(new byte[32 * 1024]);
        zeroed.RunFrames(2);
    }
}
