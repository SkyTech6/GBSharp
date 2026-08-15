using GB;
using static GB.Hardware;

/// <summary>
/// The thesis section 23 MVP, verbatim.
/// </summary>
/// <remarks>
/// The body below is exactly the snippet the thesis set as the target for the
/// compiler architecture, including <c>Sprites[0].X = x</c>. That one line needs
/// indexer lowering, a typed view over a hardware index, and property-setter
/// lowering, and it compiles to a single OAM store.
/// </remarks>
public static class Program
{
    public static void Main()
    {
        Display.Enable();

        byte x = 80;

        while (true)
        {
            if (Input.Right)
                x++;

            Sprites[0].X = x;

            Game.WaitVBlank();
        }
    }
}
