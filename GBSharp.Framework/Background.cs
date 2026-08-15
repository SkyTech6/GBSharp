namespace GB;

/// <summary>
/// The background layer: a 32x32 grid of tiles, of which 20x18 is on screen.
/// </summary>
/// <remarks>
/// <para>
/// The background is what a Game Boy game is mostly made of. Tile data is
/// loaded once into VRAM, and the map then names tiles by index, so a screen of
/// artwork costs one byte per cell rather than one byte per pixel.
/// </para>
/// <para>
/// Tile data is shared with the window layer. Loading through
/// <see cref="LoadTiles"/> or <see cref="Tiles.LoadBackground"/> makes the same
/// tiles available to both.
/// </para>
/// </remarks>
public static class Background
{
    /// <summary>Width of the tile map, in tiles. Only 20 columns are on screen.</summary>
    public const byte MapWidth = 32;

    /// <summary>Height of the tile map, in tiles. Only 18 rows are on screen.</summary>
    public const byte MapHeight = 32;

    /// <summary>
    /// Loads a converted image: its tiles, its map, and on Game Boy Color its
    /// attributes and palettes.
    /// </summary>
    /// <remarks>
    /// One call in place of the four the pieces would otherwise need, with the
    /// sizes filled in by the compiler from the image itself. The colour parts
    /// are skipped at runtime on an original Game Boy.
    /// </remarks>
    [Native("gbs_background_load")]
    public static void Load(TileMap map) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Copies a window of a larger map into the hardware map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hardware map is 32x32 cells. A map larger than that does not fit, and
    /// this is how the part you want gets there: <paramref name="sourceX"/> and
    /// <paramref name="sourceY"/> pick the top-left corner in the asset, and the
    /// rest names where it lands and how much of it to copy.
    /// </para>
    /// <para>
    /// This is the primitive a scrolling world is built from, not a camera. GB#
    /// does not keep a camera for you: that would be per-frame state you cannot
    /// see the cost of. Track the position yourself and call this when it moves.
    /// </para>
    /// <example>
    /// <code>
    /// // The screen is 20x18 tiles. Draw the part of the world at (camX, camY).
    /// Background.DrawRegion(World, 0, 0, 20, 18, camX, camY);
    /// </code>
    /// </example>
    /// </remarks>
    [Native("gbs_background_draw_region")]
    public static void DrawRegion(
        TileMap map,
        byte destinationX,
        byte destinationY,
        byte width,
        byte height,
        byte sourceX,
        byte sourceY) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Copies tile data into VRAM.
    /// </summary>
    /// <remarks>
    /// Each tile is 16 bytes. At most 255 tiles can be loaded in one call, and
    /// the background and window share a 256-tile region.
    /// </remarks>
    [Native("gbs_bkg_load_tiles")]
    public static void LoadTiles(byte firstTile, byte count, byte[] data) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Writes a rectangle of tile indices into the map.</summary>
    [Native("gbs_bkg_load_map")]
    public static void LoadMap(byte x, byte y, byte width, byte height, byte[] map) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Writes a rectangle of Game Boy Color attributes into the map.
    /// </summary>
    /// <remarks>
    /// One byte per cell: the low three bits pick a palette, bit 5 flips the
    /// tile horizontally and bit 6 vertically. Does nothing on an original
    /// Game Boy, so guard with <see cref="Palettes.IsColorHardware"/> if the
    /// game runs on both.
    /// </remarks>
    [Native("gbs_bkg_load_attributes")]
    public static void LoadAttributes(byte x, byte y, byte width, byte height, byte[] attributes) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Sets one map cell.</summary>
    [Native("gbs_bkg_set_tile")]
    public static void SetTile(byte x, byte y, byte tile) => throw FrameworkOnly.Declaration();

    /// <summary>Reads one map cell.</summary>
    [Native("get_bkg_tile_xy")]
    public static byte GetTile(byte x, byte y) => throw FrameworkOnly.Declaration();

    /// <summary>Scrolls to an absolute position.</summary>
    [Native("move_bkg")]
    public static void Move(byte x, byte y) => throw FrameworkOnly.Declaration();

    /// <summary>Scrolls by a relative amount, wrapping at the edges of the map.</summary>
    [Native("scroll_bkg")]
    public static void Scroll(sbyte dx, sbyte dy) => throw FrameworkOnly.Declaration();

    /// <summary>The horizontal scroll register.</summary>
    public static byte ScrollX
    {
        [Native("gbs_bkg_get_scroll_x")]
        get => throw FrameworkOnly.Declaration();
        [Native("gbs_bkg_set_scroll_x")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>The vertical scroll register.</summary>
    public static byte ScrollY
    {
        [Native("gbs_bkg_get_scroll_y")]
        get => throw FrameworkOnly.Declaration();
        [Native("gbs_bkg_set_scroll_y")]
        set => throw FrameworkOnly.Declaration();
    }
}
