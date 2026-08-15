using GB;
using static GB.Hardware;

// Backgrounds, palettes and audio, from data written by hand.
//
// Every byte below is in ROM because the arrays are 'static readonly'. Build
// with --emit-c and they are there as 'const uint8_t' tables; drop the readonly
// and the build report moves them into WRAM and charges you for it.
public static class Program
{
    // Four 8x8 tiles, 2 bits per pixel, 16 bytes each. Each row is two bytes:
    // the low bit of all eight pixels, then the high bit.
    private static readonly byte[] TileData =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0: empty
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 1: solid
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,

        0xAA, 0x00, 0x55, 0x00, 0xAA, 0x00, 0x55, 0x00,  // 2: dither
        0xAA, 0x00, 0x55, 0x00, 0xAA, 0x00, 0x55, 0x00,

        0xFF, 0x00, 0x81, 0x00, 0x81, 0x00, 0x81, 0x00,  // 3: box
        0x81, 0x00, 0x81, 0x00, 0x81, 0x00, 0xFF, 0x00,
    };

    // 10x9 of the 32x32 map. The rest stays tile 0.
    private static readonly byte[] Map =
    {
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 0, 0, 2, 2, 2, 2, 0, 0, 3,
        3, 0, 1, 1, 0, 0, 1, 1, 0, 3,
        3, 2, 1, 0, 0, 0, 0, 1, 2, 3,
        3, 2, 0, 0, 1, 1, 0, 0, 2, 3,
        3, 2, 1, 0, 0, 0, 0, 1, 2, 3,
        3, 0, 1, 1, 0, 0, 1, 1, 0, 3,
        3, 0, 0, 2, 2, 2, 2, 0, 0, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    };

    // One Game Boy Color palette: four 5-bit-per-channel colours.
    private static readonly ushort[] Colors = { 0x7FFF, 0x35AD, 0x1A73, 0x0000 };

    private static byte scrollX;
    private static byte scrollY;

    public static void Main()
    {
        // VRAM is only safely writable with the LCD off, or during VBlank.
        Display.Disable();

        Background.LoadTiles(0, 4, TileData);
        Background.LoadMap(0, 0, 10, 9, Map);

        // Set the DMG shades either way: on colour hardware they are ignored,
        // and on an original Game Boy they are all there is.
        Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);

        if (Palettes.IsColorHardware)
        {
            Palettes.LoadBackgroundColors(0, 1, Colors);
        }

        Audio.Enable();
        Audio.SetMasterVolume(7, 7);

        Display.Enable();
        Display.ShowBackground();

        bool wasPressed = false;

        while (true)
        {
            if (Input.Right) { scrollX++; }
            if (Input.Left) { scrollX--; }
            if (Input.Down) { scrollY++; }
            if (Input.Up) { scrollY--; }

            Background.Move(scrollX, scrollY);

            // A on the edge, not while held, so the note restarts once.
            bool pressed = Input.A;
            if (pressed && !wasPressed)
            {
                Audio.PlayTone(Channel.Pulse1, Note.A4, 12, Duty.Half);
            }
            else if (!pressed && wasPressed)
            {
                Audio.Stop(Channel.Pulse1);
            }

            wasPressed = pressed;

            Game.WaitVBlank();
        }
    }
}
