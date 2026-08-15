using GB;

// A PNG on disk, on screen.
//
// Nothing here converts anything at runtime. forest.png is decoded, checked
// against the hardware's limits, reduced to 2bpp tiles, deduplicated, turned
// into a map and a set of colour palettes, and written into the ROM while this
// project builds. The field below is a name for that data.
//
// Build with --emit-c to see the tables, or read the build report to see what
// they cost.
public static class Program
{
    [Asset("forest.png")]
    private static TileMap Forest;

    private static byte scroll;

    public static void Main()
    {
        Display.Disable();

        // Tiles, map, colour palettes and the attribute map, in one call. The
        // sizes come from the image, so there is nothing here to keep in sync.
        Background.Load(Forest);

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
