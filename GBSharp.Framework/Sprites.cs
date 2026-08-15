namespace GB;

/// <summary>
/// The hardware, as values you can name.
/// </summary>
/// <remarks>
/// <para>
/// Game code brings these into scope with <c>using static GB.Hardware;</c> and
/// then writes <c>Sprites[0].X = x</c>.
/// </para>
/// <para>
/// These are fields rather than properties, and the indexer below returns by
/// reference, because C# has no static indexers and will not let a property be
/// assigned through an rvalue. None of it survives compilation: every member
/// here is <see cref="NativeIdentityAttribute"/> and erases during lowering,
/// leaving only the sprite index in the generated C.
/// </para>
/// </remarks>
public static class Hardware
{
    /// <summary>The 40 hardware sprites in OAM.</summary>
    [NativeIdentity]
    public static SpriteTable Sprites;

    /// <summary>The eight Game Boy Color background palettes.</summary>
    [NativeIdentity]
    public static BackgroundPaletteTable BackgroundPalettes;

    /// <summary>The eight Game Boy Color sprite palettes.</summary>
    [NativeIdentity]
    public static SpritePaletteTable SpritePalettes;
}

/// <summary>
/// The attribute bits on a hardware sprite.
/// </summary>
/// <remarks>
/// These are the raw OAM flags. <see cref="SpriteRef"/> exposes each one as a
/// named property; this enum is for setting several at once.
/// </remarks>
[System.Flags]
public enum SpriteFlags : byte
{
    None = 0,

    /// <summary>Game Boy Color: take tile data from VRAM bank 1.</summary>
    Bank = 0x08,

    /// <summary>Original Game Boy: use sprite palette 1 rather than 0.</summary>
    Palette = 0x10,

    /// <summary>Mirror horizontally.</summary>
    FlipX = 0x20,

    /// <summary>Mirror vertically.</summary>
    FlipY = 0x40,

    /// <summary>Draw behind background colours 1-3.</summary>
    BehindBackground = 0x80,
}

/// <summary>
/// Indexed access to OAM. Holds no state and occupies no memory.
/// </summary>
public struct SpriteTable
{
    /// <summary>The number of hardware sprites. Indices beyond this do nothing.</summary>
    public const byte Count = 40;

    /// <summary>
    /// A typed view of one sprite. Costs nothing: this lowers to
    /// <paramref name="index"/> itself.
    /// </summary>
    [NativeIdentity]
    public ref SpriteRef this[byte index] => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Sets a sprite's position in one call.
    /// </summary>
    /// <remarks>
    /// Cheaper than assigning <see cref="SpriteRef.X"/> and
    /// <see cref="SpriteRef.Y"/> separately, which costs two OAM writes.
    /// </remarks>
    [Native("gbs_sprite_move")]
    public void Move(byte id, byte x, byte y) => throw FrameworkOnly.Declaration();

    /// <summary>Sets which tile a sprite draws.</summary>
    [Native("gbs_sprite_set_tile")]
    public void SetTile(byte id, byte tile) => throw FrameworkOnly.Declaration();

    /// <summary>Moves a sprite off screen.</summary>
    [Native("gbs_sprite_hide")]
    public void Hide(byte id) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Moves every sprite off screen.
    /// </summary>
    /// <remarks>
    /// This is a loop over all 40, not a single register write. Worth doing once
    /// at startup; not worth doing every frame.
    /// </remarks>
    [Native("gbs_sprites_hide_all")]
    public void HideAll() => throw FrameworkOnly.Declaration();

    /// <summary>Moves a sprite by a relative amount.</summary>
    [Native("scroll_sprite")]
    public void Scroll(byte id, sbyte dx, sbyte dy) => throw FrameworkOnly.Declaration();

    /// <summary>Sets all of a sprite's attribute bits at once.</summary>
    [Native("set_sprite_prop")]
    public void SetFlags(byte id, SpriteFlags flags) => throw FrameworkOnly.Declaration();

    /// <summary>Copies sprite tile data into VRAM. 16 bytes per tile.</summary>
    [Native("gbs_sprite_load_tiles")]
    public void LoadTiles(byte firstTile, byte count, byte[] data) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Loads a converted sprite sheet: its tiles, and on Game Boy Color its
    /// palettes.
    /// </summary>
    [Native("gbs_sprite_load")]
    public void Load(SpriteAsset sheet) => throw FrameworkOnly.Declaration();
}

/// <summary>
/// A reference to one hardware sprite by index.
/// </summary>
/// <remarks>
/// This struct holds no state. Each property reads or writes OAM directly for
/// the sprite it names, so assigning <see cref="X"/> and <see cref="Y"/>
/// separately costs two OAM writes where <see cref="SpriteTable.Move"/> costs one.
/// </remarks>
public struct SpriteRef
{
    /// <summary>Screen X plus 8. A sprite at X = 0 is fully off the left edge.</summary>
    public byte X
    {
        [Native("gbs_sprite_get_x")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_x")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>Screen Y plus 16. A sprite at Y = 0 is fully off the top edge.</summary>
    public byte Y
    {
        [Native("gbs_sprite_get_y")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_y")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>The tile this sprite draws.</summary>
    public byte Tile
    {
        [Native("gbs_sprite_get_tile")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_tile")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>All of this sprite's attribute bits.</summary>
    public SpriteFlags Flags
    {
        [Native("get_sprite_prop")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("set_sprite_prop")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>Mirrors this sprite horizontally.</summary>
    public bool FlipX
    {
        [Native("gbs_sprite_get_flip_x")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_flip_x")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>Mirrors this sprite vertically.</summary>
    public bool FlipY
    {
        [Native("gbs_sprite_get_flip_y")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_flip_y")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>Draws this sprite behind background colours 1-3.</summary>
    public bool BehindBackground
    {
        [Native("gbs_sprite_get_priority")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_priority")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>
    /// Which of the eight Game Boy Color sprite palettes this sprite uses.
    /// </summary>
    /// <remarks>
    /// Game Boy Color only. On an original Game Boy the choice is between two
    /// palettes and is made with <see cref="UseSecondPalette"/>, a different
    /// bit, which is why these are two properties rather than one that would
    /// mean something different on each machine.
    /// </remarks>
    public byte Palette
    {
        [Native("gbs_sprite_get_palette")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_palette")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>
    /// Original Game Boy: use sprite palette 1 rather than 0.
    /// </summary>
    /// <remarks>
    /// See <see cref="Palette"/> for the Game Boy Color equivalent.
    /// </remarks>
    public bool UseSecondPalette
    {
        [Native("gbs_sprite_get_dmg_palette")]
        readonly get => throw FrameworkOnly.Declaration();
        [Native("gbs_sprite_set_dmg_palette")]
        set => throw FrameworkOnly.Declaration();
    }
}
