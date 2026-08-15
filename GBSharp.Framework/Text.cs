namespace GB;

/// <summary>
/// Drawing text from a <see cref="FontAsset"/>, tile by tile.
/// </summary>
/// <remarks>
/// <para>
/// There is no cursor, no scrolling, and no <c>printf</c>. A string is a row of
/// tiles like any other background artwork: one call uploads the glyphs once,
/// and another writes them into the map wherever the game wants them. GBDK's
/// own <c>gbdk/font.h</c> and <c>console.h</c> exist for the opposite choice - a
/// cursor and scrolling state the game does not control - which is exactly the
/// kind of hardware-hiding layer GB# does not add. GB# abstracts boilerplate,
/// not hardware.
/// </para>
/// <para>
/// GB#'s subset has no <see cref="System.String"/> (GBS0043), so there is no
/// string literal to pass here yet. Write the bytes directly:
/// <c>static readonly byte[] Label = { 72, 69, 76, 76, 79 };</c>. Sugar that
/// turns a string literal into that array at compile time is real, separate
/// work - a language-subset change with its own review - and is deliberately
/// not part of this.
/// </para>
/// </remarks>
public static class Text
{
    /// <summary>Uploads a font's glyph tiles into the background/window tile region.</summary>
    [Native("gbs_font_load")]
    public static void Load(FontAsset font, byte firstTile) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Draws <paramref name="length"/> bytes of <paramref name="text"/> as
    /// tiles, left to right starting at <paramref name="x"/>, <paramref name="y"/>.
    /// </summary>
    /// <remarks>
    /// Each byte is a character code, looked up in the font's glyph table and
    /// written as a background tile - the same map write <see cref="Background.SetTile"/>
    /// makes, just several in a row. A code the font's <c>Characters</c> did not
    /// declare draws whichever glyph is tile 0, rather than being checked here:
    /// see <see cref="FontAttribute"/>.
    /// </remarks>
    [Native("gbs_font_draw")]
    public static void Draw(FontAsset font, byte firstTile, byte x, byte y, byte length, byte[] text) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Same as <see cref="Draw"/>, onto the window layer instead of the background.</summary>
    [Native("gbs_win_font_draw")]
    public static void DrawWindow(FontAsset font, byte firstTile, byte x, byte y, byte length, byte[] text) =>
        throw FrameworkOnly.Declaration();
}
