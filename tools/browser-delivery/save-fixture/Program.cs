using GB;

// A deliberately small persistence fixture, using native cartridge RAM access
// instead of adding a save API to the framework. X increments a saved byte.
public static class Program
{
    [Native("fixture_init")]
    private static void Initialize() { }

    [Native("fixture_tick")]
    private static void Tick() { }

    public static void Main()
    {
        BankedFixture.Touch();
        Initialize();
        while (true)
        {
            Game.WaitVBlank();
            Tick();
        }
    }
}

[Bank(1)]
public static class BankedFixture
{
    public static byte Touch() => 1;
}
