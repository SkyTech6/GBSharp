using GB;
using static GB.Hardware;

/// <summary>
/// The GB# vertical spike: C# to a ROM that boots and responds to input.
/// </summary>
/// <remarks>
/// This is the thesis section 23 MVP, using Sprites.Move to set position in one
/// OAM write. Samples/MoveSprite is the same program written with the
/// Sprites[0].X indexer form.
/// </remarks>
public static class Program
{
    public static void Main()
    {
        Display.Enable();
        Display.ShowSprites();

        Sprites.SetTile(0, 1);

        byte x = 80;
        byte y = 72;

        while (true)
        {
            if (Input.Right)
            {
                x++;
            }

            if (Input.Left)
            {
                x--;
            }

            if (Input.Down)
            {
                y++;
            }

            if (Input.Up)
            {
                y--;
            }

            Sprites.Move(0, x, y);

            Game.WaitVBlank();
        }
    }
}
