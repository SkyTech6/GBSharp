using GB;

// A cartridge larger than 32 KB, and the three ways to say where something goes.
//
// Bank 0 is always mapped and is where everything starts. It holds the frame
// loop and anything that runs every frame, because reaching a banked function
// costs a bank switch each way. Everything else is worth moving out: bank 0 is
// the one space no larger cartridge can give you more of.

public static class Program
{
    // Not banked. Read every frame, so it stays where it is already mapped.
    private static byte scroll;

    public static void Main()
    {
        Display.Enable();

        // A banked call. GB# reports the cost of this line at build time.
        ForestLevel.Load();

        while (true)
        {
            scroll++;
            Background.Scroll(1, 0);

            Game.WaitVBlank();
        }
    }
}

// An explicit bank. The generated C says '#pragma bank 2' on its first line, and
// the build report lists what landed there.
[Bank(2)]
public static class ForestLevel
{
    // The asset's tiles, map, attributes and palettes all move to bank 2 with
    // it. Background.Load is handed the bank alongside the pointers, so it maps
    // the data in before reading and restores the previous bank afterwards.
    [Asset("forest.png")]
    private static TileMap Art;

    public static void Load() => Background.Load(Art);
}

// No number: the build chooses a bank and then tells you which, so this can be
// pinned with [Bank(n)] once the layout settles.
[Bank]
public static class Credits
{
    private static readonly byte[] Text =
    {
        0x47, 0x42, 0x23, 0x20, 0x53, 0x41, 0x4D, 0x50, 0x4C, 0x45,
    };

    public static byte Initial() => Text[0];
}
