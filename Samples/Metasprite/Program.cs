using GB;
using static GB.Hardware;

// A 32x16 sheet: two 2x2-tile frames, each missing one sub-sprite - blank,
// palette index 0, the colour real hardware never draws for a sprite. That
// sub-sprite costs no OAM entry and no frame-table byte; --emit-c and read
// Program_Hero_frames to see it: three metasprite_t records per frame, not
// four, each ended by GBDK's own terminator.
//
// Frame 0's blank corner is bottom-right; frame 1's is top-right, so
// alternating between them reads as a two-frame step animation.
public static class Program
{
    [Metasprite("hero.png", FrameWidth = 2, FrameHeight = 2)]
    private static MetaspriteAsset Hero;

    private static byte x = 80;
    private static byte frame;
    private static byte usedLastFrame;

    public static void Main()
    {
        Display.Enable();
        Display.ShowSprites();

        Metasprites.Load(Hero);

        while (true)
        {
            if (Input.Right) { x++; }
            if (Input.Left) { x--; }

            byte used = Metasprites.Move(Hero, frame, 0, 0, x, 80);

            // Frames can use different numbers of sub-sprites; hide whatever
            // the last frame drew that this one did not reuse.
            if (used < usedLastFrame)
            {
                Metasprites.HideRange(used, usedLastFrame);
            }

            usedLastFrame = used;

            Game.WaitVBlank();

            frame = frame == 0 ? (byte)1 : (byte)0;
        }
    }
}
