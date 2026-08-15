using GB;

// The same pipeline, on an original Game Boy.
//
// cave.png is drawn in four greys, which is all a DMG can show. The converter
// orders them lightest first to match the hardware's default palette, so the
// image is right before SetBackgroundShades is ever called, and rearranging
// that call is how you invert the picture without touching the artwork.
//
// No attribute map and no colour tables are generated for this target, so the
// same image costs less ROM here than it would on Game Boy Color.
public static class Program
{
    [Asset("cave.png")]
    private static TileMap Cave;

    private static byte scroll;

    public static void Main()
    {
        Display.Disable();
        Background.Load(Cave);
        Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
        Display.Enable();
        Display.ShowBackground();

        while (true)
        {
            if (Input.Right) { scroll++; }
            if (Input.Left) { scroll--; }

            Background.Move(scroll, 0);
            Game.WaitVBlank();
        }
    }
}
