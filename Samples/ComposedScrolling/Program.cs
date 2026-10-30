using GB;

// D-pad explores a 32x32 world through a 20x18 hardware-map ring.
// Graphics are uploaded once. Only newly exposed map cells are copied.
public static class Program
{
    [Font("font.png", Characters = "0123456789HI")]
    private static FontAsset Font;
    [Metasprite("hero.png", FrameWidth = 2, FrameHeight = 2)]
    private static MetaspriteAsset Hero;
    [Metasprite("enemy.png", FrameWidth = 2, FrameHeight = 2)]
    private static MetaspriteAsset Enemy;
    private static readonly byte[] Label = { 72, 73, 48, 49 };
    private static readonly ushort[] HudColors = { 32767, 22197, 10570, 0 };
    private static readonly byte[] HudAttributes = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    private static byte cameraX;
    private static byte cameraY;
    private static byte delay;

    public static void Main()
    {
        Display.Disable();
        Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
        Palettes.SetSpriteShades(0, Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
        Palettes.SetSpriteShades(1, Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
        World.Initialize();
        Text.Load(Font, 128);
        Palettes.LoadBackgroundColors(1, 1, HudColors);
        // Set attributes explicitly: text drawing only writes tile indices.
        for (byte x = 0; x < 20; x++)
        {
            Window.SetTile(x, 0, 128);
            Window.SetTile(x, 1, 128);
        }
        Text.DrawWindow(Font, 128, 1, 0, 4, Label);
        Window.LoadAttributes(0, 0, 20, 1, HudAttributes);
        Window.LoadAttributes(0, 1, 20, 1, HudAttributes);
        Window.Move(7, 128);
        Metasprites.Load(Hero, 192, 0);
        Metasprites.Load(Enemy, 196, 1);
        Metasprites.Move(Hero, 0, 192, 0, 80, 80, 0);
        Metasprites.Move(Enemy, 0, 196, 4, 128, 96, 1);
        Display.ShowBackground();
        Display.ShowWindow();
        Display.ShowSprites();
        Display.Enable();
        while (true)
        {
            Game.WaitVBlank();
            delay++;
            if (delay == 8)
            {
                delay = 0;
                // One exposed strip per update keeps the work bounded.
                if (Input.Right && cameraX < 12)
                {
                    cameraX++;
                    World.Column((byte)(cameraX + 19), cameraY);
                }
                else if (Input.Left && cameraX > 0)
                {
                    cameraX--;
                    World.Column(cameraX, cameraY);
                }
                else if (Input.Down && cameraY < 14)
                {
                    cameraY++;
                    World.Row(cameraX, (byte)(cameraY + 17));
                }
                else if (Input.Up && cameraY > 0)
                {
                    cameraY--;
                    World.Row(cameraX, cameraY);
                }
                Background.Move((byte)(cameraX * 8), (byte)(cameraY * 8));
            }
        }
    }
}

[Bank(2)]
public static class World
{
    [Asset("world.png")]
    private static TileMap Map;

    public static void Initialize()
    {
        Background.LoadGraphics(Map, 0, 0);
        Background.CopyRegion(Map, 0, 0, 20, 18, 0, 0, 0, 0);
    }

    public static void Column(byte x, byte y) =>
        Background.CopyRegion(Map, x, y, 1, 18, x, y, 0, 0);

    public static void Row(byte x, byte y) =>
        Background.CopyRegion(Map, x, y, 20, 1, x, y, 0, 0);
}
