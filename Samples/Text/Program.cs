using GB;

/// <summary>
/// Draws a static label and a counter that ticks once a second, from one
/// font sheet. Text is tiles placed on the background, not a console: there is
/// no cursor and nothing scrolls on its own.
/// </summary>
/// <remarks>
/// GB#'s subset has no <see cref="string"/> (GBS0043), so every byte here is a
/// character code written out by hand - see <see cref="Text"/>'s remarks for
/// why, and for the not-yet-done sugar that would let a string literal fill
/// an array like this automatically.
/// </remarks>
public static class Program
{
    [Font("font.png", Characters = "0123456789HI")]
    private static FontAsset Digits;

    // "HI", as the character codes gbs_font_draw indexes the glyph table with.
    private static readonly byte[] Greeting = { 72, 73 };

    private static byte[] counter = { 48, 48 }; // "00"
    private static byte frames;
    private static byte value;

    public static void Main()
    {
        Display.Disable();
        Text.Load(Digits, 0);
        Display.Enable();
        Display.ShowBackground();

        Text.Draw(Digits, 0, 1, 1, 2, Greeting);
        Text.Draw(Digits, 0, 1, 3, 2, counter);

        while (true)
        {
            Game.WaitVBlank();
            frames++;

            if (frames == 60)
            {
                frames = 0;
                value++;

                if (value == 100)
                {
                    value = 0;
                }

                counter[0] = (byte)(48 + (value / 10));
                counter[1] = (byte)(48 + (value % 10));

                Text.Draw(Digits, 0, 1, 3, 2, counter);
            }
        }
    }
}
