namespace GB;

/// <summary>
/// The window layer: a second tile map drawn on top of the background.
/// </summary>
/// <remarks>
/// The window does not scroll with the background, which is what makes it the
/// right place for a status bar or a dialogue box. It has no tile data of its
/// own: it draws from the same VRAM the background uses, so load through
/// <see cref="Tiles.LoadBackground"/> and both layers see the result.
/// </remarks>
public static class Window
{
    /// <summary>
    /// The X position that puts the window's left edge at the left of the screen.
    /// </summary>
    /// <remarks>
    /// The window's X register is offset by 7. Values below this push it off the
    /// left edge, and 7 is the usual setting for a full-width window.
    /// </remarks>
    public const byte MinX = 7;

    /// <summary>
    /// Loads a converted image: its tiles, its map, and on Game Boy Color its
    /// attributes and palettes, onto the window layer instead of the
    /// background. See <see cref="Background.Load(TileMap)"/>.
    /// </summary>
    [Native("gbs_window_load")]
    public static void Load(TileMap map) => throw FrameworkOnly.Declaration();

    /// <summary>Loads converted graphics and the map at explicit tile and CGB palette locations.</summary>
    /// <remarks>Invalid placements do nothing. Background and window share tile data and palettes.</remarks>
    [Native("gbs_window_load_at")]
    public static void Load(TileMap map, byte firstTile, byte firstPalette) => throw FrameworkOnly.Declaration();

    /// <summary>Uploads only converted tiles and palettes; does not change the hardware map.</summary>
    [Native("gbs_window_load_graphics")]
    public static void LoadGraphics(TileMap map, byte firstTile, byte firstPalette) => throw FrameworkOnly.Declaration();

    /// <summary>Copies converted map cells without uploading graphics or palettes.</summary>
    /// <remarks>
    /// Source must fit the asset; width and height must be at most 32. Destination coordinates
    /// wrap modulo 32. Invalid arguments do nothing. Tile and palette offsets must match
    /// LoadGraphics. CGB attribute flags are preserved; palette attributes are ignored on DMG.
    /// Call during safe VRAM update time and keep camera and scheduling state in game code.
    /// </remarks>
    [Native("gbs_window_copy_region")]
    public static void CopyRegion(TileMap map, byte destinationX, byte destinationY,
        byte width, byte height, byte sourceX, byte sourceY, byte firstTile, byte firstPalette) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Writes a rectangle of tile indices into the window map.</summary>
    [Native("gbs_win_load_map")]
    public static void LoadMap(byte x, byte y, byte width, byte height, byte[] map) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Writes a rectangle of Game Boy Color attributes into the window map.
    /// </summary>
    /// <remarks>
    /// One byte per cell: the low three bits pick a palette, bit 5 flips the
    /// tile horizontally and bit 6 vertically. Does nothing on an original
    /// Game Boy, so guard with <see cref="Palettes.IsColorHardware"/> if the
    /// game runs on both.
    /// </remarks>
    [Native("gbs_win_load_attributes")]
    public static void LoadAttributes(byte x, byte y, byte width, byte height, byte[] attributes) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Sets one window map cell.</summary>
    [Native("gbs_win_set_tile")]
    public static void SetTile(byte x, byte y, byte tile) => throw FrameworkOnly.Declaration();

    /// <summary>Reads one window map cell.</summary>
    [Native("get_win_tile_xy")]
    public static byte GetTile(byte x, byte y) => throw FrameworkOnly.Declaration();

    /// <summary>Positions the window. See <see cref="MinX"/> for the X offset.</summary>
    [Native("move_win")]
    public static void Move(byte x, byte y) => throw FrameworkOnly.Declaration();

    /// <summary>Moves the window by a relative amount.</summary>
    [Native("scroll_win")]
    public static void Scroll(sbyte dx, sbyte dy) => throw FrameworkOnly.Declaration();

    /// <summary>The window's X register, offset by 7. See <see cref="MinX"/>.</summary>
    public static byte X
    {
        [Native("gbs_win_get_x")]
        get => throw FrameworkOnly.Declaration();
        [Native("gbs_win_set_x")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>The window's Y register, in screen pixels from the top.</summary>
    public static byte Y
    {
        [Native("gbs_win_get_y")]
        get => throw FrameworkOnly.Declaration();
        [Native("gbs_win_set_y")]
        set => throw FrameworkOnly.Declaration();
    }
}
