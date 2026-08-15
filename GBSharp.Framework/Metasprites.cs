namespace GB;

/// <summary>
/// Drawing an animated hardware sprite made of several sub-sprites, from a
/// <c>[Metasprite]</c> asset.
/// </summary>
/// <remarks>
/// Thin wrappers over GBDK's own <c>move_metasprite_ex</c> family
/// (<c>gb/metasprites.h</c>). Which hardware sprites and which tiles a frame
/// uses are arguments on every call, never implicit state this class keeps
/// for you.
/// </remarks>
public static class Metasprites
{
    /// <summary>Uploads a sheet's tiles and, on Game Boy Color, its palettes.</summary>
    [Native("gbs_metasprite_load")]
    public static void Load(MetaspriteAsset sheet) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Moves one frame of a metasprite to the absolute position x, y.
    /// </summary>
    /// <param name="sheet">The converted sheet, already loaded with <see cref="Load"/>.</param>
    /// <param name="frame">Which frame to draw, in the order the sheet's frames were declared.</param>
    /// <param name="baseTile">The first tile this frame's sub-sprites were uploaded to.</param>
    /// <param name="baseSprite">The first hardware sprite to use; the frame uses as many as it needs from there.</param>
    /// <param name="x">Absolute screen X of the frame's origin, in pixels.</param>
    /// <param name="y">Absolute screen Y of the frame's origin, in pixels.</param>
    /// <returns>How many hardware sprites this frame used.</returns>
    /// <remarks>
    /// Different frames of the same metasprite can use different numbers of
    /// sub-sprites. When switching frames, hide the ones the previous frame
    /// used and this one did not - <see cref="HideRange"/> from this frame's
    /// return value onward, before drawing the new one, does that.
    /// </remarks>
    [Native("gbs_metasprite_move")]
    public static byte Move(MetaspriteAsset sheet, byte frame, byte baseTile, byte baseSprite, byte x, byte y) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Same as <see cref="Move"/>, mirrored horizontally.</summary>
    [Native("gbs_metasprite_move_flip_x")]
    public static byte MoveFlippedX(MetaspriteAsset sheet, byte frame, byte baseTile, byte baseSprite, byte x, byte y) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Same as <see cref="Move"/>, mirrored vertically.</summary>
    [Native("gbs_metasprite_move_flip_y")]
    public static byte MoveFlippedY(MetaspriteAsset sheet, byte frame, byte baseTile, byte baseSprite, byte x, byte y) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Same as <see cref="Move"/>, mirrored both ways.</summary>
    [Native("gbs_metasprite_move_flip_xy")]
    public static byte MoveFlippedXy(MetaspriteAsset sheet, byte frame, byte baseTile, byte baseSprite, byte x, byte y) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Hides hardware sprites <paramref name="from"/> up to (not including)
    /// <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// Frames vary in how many sub-sprites they need. After moving to a new
    /// frame, hide from its <see cref="Move"/> return value up to the previous
    /// frame's, or the sprites that frame is no longer using stay on screen.
    /// </remarks>
    [Native("hide_sprites_range")]
    public static void HideRange(byte from, byte to) => throw FrameworkOnly.Declaration();
}
