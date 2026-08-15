using System.Security.Cryptography;
using System.Text;
using GB;
using GBSharp.Emulator;

namespace GBSharp.Tests;

/// <summary>
/// Runs a ROM and asserts on what the hardware did.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, the integration tests could assert that GBDK linked
/// something with a valid header and no more than that. Whether GB# generated
/// correct code was unverified. This closes that hole: a test compiles C#,
/// links a ROM, boots it, presses buttons, and reads the memory the program
/// wrote.
/// </para>
/// <para>
/// Wraps <see cref="GameBoy"/> rather than extending it, because the ABI is
/// deliberately free of conveniences and this is where the conveniences belong.
/// </para>
/// </remarks>
public sealed class GameBoyTest : IDisposable
{
    /// <summary>
    /// Set this environment variable to insist the emulator runtime is present.
    /// </summary>
    public const string RequireEmulatorVariable = "GBSHARP_REQUIRE_EMULATOR";

    /// <summary>Where sprite 0's attributes live in OAM.</summary>
    /// <remarks>
    /// Four bytes per sprite: Y, X, tile, flags. GBDK writes sprites to a
    /// shadow copy in RAM and DMAs it into OAM each VBlank, so these addresses
    /// hold what the program set as of its last <c>Game.WaitVBlank()</c>.
    /// </remarks>
    public const ushort ObjectAttributeMemory = 0xFE00;

    private readonly GameBoy game;

    private GameBoyTest(GameBoy game) => this.game = game;

    /// <summary>
    /// Boots a ROM from disk.
    /// </summary>
    /// <remarks>
    /// Reading the file happens here rather than in <see cref="GameBoy"/>: the
    /// ABI takes bytes and never a path, so that the same call serves a test, a
    /// player reading a ROM out of its own executable, and a browser with no
    /// filesystem at all.
    /// </remarks>
    public static GameBoyTest Load(string romPath)
    {
        // The instrumented flavour, which is what M5's tooling will want and
        // what makes a failing test inspectable. Idempotent, so every test may
        // call it.
        EmulatorRuntime.Load(EmulatorFlavour.Debug);

        return new GameBoyTest(GameBoy.Load(File.ReadAllBytes(romPath)));
    }

    /// <summary>Boots a ROM already in memory.</summary>
    public static GameBoyTest Load(ReadOnlySpan<byte> rom)
    {
        EmulatorRuntime.Load(EmulatorFlavour.Debug);
        return new GameBoyTest(GameBoy.Load(rom));
    }

    /// <summary>The machine underneath, for anything this class does not wrap.</summary>
    public GameBoy Machine => game;

    /// <summary>Runs one frame.</summary>
    public void RunFrame() => game.RunFrame();

    /// <summary>Runs <paramref name="count"/> frames.</summary>
    public void RunFrames(int count) => game.RunFrames(count);

    /// <summary>
    /// Holds one or more buttons down. They stay held until <see cref="Release"/>.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="Button"/>, the same mask the game reads through
    /// <c>Input.Read()</c>, so a test presses the button the program is looking
    /// for rather than a second enumeration of the same hardware.
    /// </remarks>
    public void Press(Button buttons) => Set(buttons, pressed: true);

    /// <summary>Releases one or more buttons.</summary>
    public void Release(Button buttons) => Set(buttons, pressed: false);

    /// <summary>
    /// Holds buttons for a number of frames and releases them, which is what
    /// "the player pressed start" means to a program that polls each frame.
    /// </summary>
    public void Tap(Button buttons, int frames = 2)
    {
        Press(buttons);
        RunFrames(frames);
        Release(buttons);
    }

    /// <summary>Reads a byte of the emulated address space.</summary>
    public byte ReadMemory(ushort address) => game.ReadMemory(address);

    /// <summary>Writes a byte of the emulated address space.</summary>
    public void WriteMemory(ushort address, byte value) => game.WriteMemory(address, value);

    /// <summary>The address the CPU is about to execute.</summary>
    public ushort ProgramCounter => game.ProgramCounter;

    /// <summary>
    /// The ROM bank under <paramref name="address"/>, or <c>-1</c> when the
    /// address is not in the cartridge.
    /// </summary>
    public int RomBankAt(ushort address) => game.RomBankAt(address);

    /// <summary>Reads one of sprite <paramref name="index"/>'s four OAM bytes.</summary>
    public byte ReadSprite(int index, SpriteByte which) =>
        game.ReadMemory((ushort)(ObjectAttributeMemory + (index * 4) + (int)which));

    /// <summary>The screen as it stands, 160 by 144, each pixel 0xAABBGGRR.</summary>
    public ReadOnlySpan<uint> Screen => game.Framebuffer;

    /// <summary>
    /// The screen as a NetPBM P3 image, byte for byte what binjgb's own
    /// <c>tester.c</c> writes.
    /// </summary>
    /// <remarks>
    /// The format matters because upstream records a SHA1 of exactly this text
    /// for every ROM in its compatibility suite. Producing the identical bytes
    /// is what lets a GB# test assert against those recorded hashes instead of
    /// against a hash of its own, which would only ever prove that nothing
    /// changed since the day it was recorded.
    /// </remarks>
    public string ScreenAsPpm()
    {
        ReadOnlySpan<uint> pixels = Screen;
        var text = new StringBuilder(GameBoy.ScreenWidth * GameBoy.ScreenHeight * 12);

        text.Append($"P3\n{GameBoy.ScreenWidth} {GameBoy.ScreenHeight}\n255\n");

        for (int y = 0; y < GameBoy.ScreenHeight; y++)
        {
            for (int x = 0; x < GameBoy.ScreenWidth; x++)
            {
                uint pixel = pixels[(y * GameBoy.ScreenWidth) + x];
                text.Append($"{pixel & 0xFF,3} {(pixel >> 8) & 0xFF,3} {(pixel >> 16) & 0xFF,3} ");
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// SHA1 of <see cref="ScreenAsPpm"/>, which is the identifier binjgb's
    /// <c>scripts/test.json</c> records per test ROM.
    /// </summary>
    public string ScreenHash() =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(ScreenAsPpm())));

    public void Dispose() => game.Dispose();

    private void Set(Button buttons, bool pressed)
    {
        foreach ((Button mask, GameBoyButton button) in ButtonMap)
        {
            if ((buttons & mask) != 0)
            {
                game.SetButton(button, pressed);
            }
        }
    }

    /// <summary>
    /// <see cref="Button"/> is a mask of GBDK's <c>J_*</c> bits and
    /// <see cref="GameBoyButton"/> is the ABI's index, so pressing takes a
    /// translation. Kept as a table so a mask naming several buttons works.
    /// </summary>
    private static readonly (Button Mask, GameBoyButton Button)[] ButtonMap =
    [
        (Button.Right, GameBoyButton.Right),
        (Button.Left, GameBoyButton.Left),
        (Button.Up, GameBoyButton.Up),
        (Button.Down, GameBoyButton.Down),
        (Button.A, GameBoyButton.A),
        (Button.B, GameBoyButton.B),
        (Button.Select, GameBoyButton.Select),
        (Button.Start, GameBoyButton.Start),
    ];

    /// <summary>Which of a sprite's four OAM bytes to read.</summary>
    public enum SpriteByte
    {
        Y = 0,
        X = 1,
        Tile = 2,
        Flags = 3,
    }

    /// <summary>True when the native runtime is available for integration tests.</summary>
    /// <remarks>
    /// The same convention as <see cref="TestHarness.GbdkAvailable"/>, and for
    /// the same reason: xUnit v2 has no runtime skip, so the tests return early
    /// when this is false, which keeps a bare checkout green and would also turn
    /// a broken library lookup into a silent pass. Setting
    /// <see cref="RequireEmulatorVariable"/> makes an absent runtime a loud
    /// failure, which is what CI does once it has fetched one.
    /// </remarks>
    public static bool EmulatorAvailable
    {
        get
        {
            bool located = EmulatorRuntime.TryLocate(null, out string? root, out IReadOnlyList<string> searched)
                && root is not null;

            if (!located && RequireEmulator)
            {
                throw new InvalidOperationException(
                    $"{RequireEmulatorVariable} is set, but no emulator runtime was found. " +
                    "The integration tests would otherwise have skipped themselves silently. Looked in: " +
                    string.Join(", ", searched.Take(4)));
            }

            return located;
        }
    }

    private static bool RequireEmulator =>
        TestHarness.IsRequireValue(Environment.GetEnvironmentVariable(RequireEmulatorVariable));
}
