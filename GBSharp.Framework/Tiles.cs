namespace GB;

/// <summary>
/// Loading tile data into VRAM.
/// </summary>
/// <remarks>
/// There are two tile regions, not three. The background and window share one;
/// sprites have their own. <see cref="LoadBackground"/> and
/// <see cref="Background.LoadTiles"/> are deliberately the same operation under
/// two names: one for code that thinks in layers, one for code that thinks in
/// tiles, and neither costs anything the other does not.
/// </remarks>
public static class Tiles
{
    /// <summary>Bytes per 8x8 tile, at 2 bits per pixel.</summary>
    public const byte BytesPerTile = 16;

    /// <summary>The number of tiles a layer's VRAM region holds.</summary>
    public const byte MaxTiles = 255;

    /// <summary>
    /// Loads background and window tiles. Same as <see cref="Background.LoadTiles"/>.
    /// </summary>
    [Native("gbs_bkg_load_tiles")]
    public static void LoadBackground(byte firstTile, byte count, byte[] data) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Loads window tiles.
    /// </summary>
    /// <remarks>
    /// The window draws from the background's tile region, so this writes the
    /// same VRAM as <see cref="LoadBackground"/>. It exists so window-only code
    /// can say what it means.
    /// </remarks>
    [Native("gbs_win_load_tiles")]
    public static void LoadWindow(byte firstTile, byte count, byte[] data) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Loads sprite tiles, which live in their own VRAM region.</summary>
    [Native("gbs_sprite_load_tiles")]
    public static void LoadSprite(byte firstTile, byte count, byte[] data) =>
        throw FrameworkOnly.Declaration();
}
