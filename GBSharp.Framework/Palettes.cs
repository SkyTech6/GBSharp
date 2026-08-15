namespace GB;

/// <summary>One of the four shades an original Game Boy can draw.</summary>
public enum Shade : byte
{
    White = 0,
    LightGray = 1,
    DarkGray = 2,
    Black = 3,
}

/// <summary>
/// Colour, on both machines.
/// </summary>
/// <remarks>
/// <para>
/// The two machines work differently and this class does not pretend otherwise.
/// An original Game Boy has one background palette and two sprite palettes,
/// each mapping the four values in a tile to four fixed shades. A Game Boy
/// Color has eight background and eight sprite palettes of four real colours,
/// chosen per tile through the attribute map.
/// </para>
/// <para>
/// A game that runs on both sets the shades unconditionally and the colours
/// behind <see cref="IsColorHardware"/>: the colour calls do nothing on a DMG,
/// but the data they would have read still costs ROM.
/// </para>
/// </remarks>
public static class Palettes
{
    /// <summary>Background palettes available on Game Boy Color.</summary>
    public const byte ColorPaletteCount = 8;

    /// <summary>Colours in one palette, on either machine.</summary>
    public const byte ColorsPerPalette = 4;

    /// <summary>True when running on Game Boy Color hardware.</summary>
    [Native("gbs_is_color")]
    public static bool IsColorHardware => throw FrameworkOnly.Declaration();

    /// <summary>Maps the four tile values to shades, for the background.</summary>
    [Native("gbs_set_bkg_shades")]
    public static void SetBackgroundShades(Shade c0, Shade c1, Shade c2, Shade c3) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Maps the four tile values to shades, for one of the two sprite palettes.
    /// </summary>
    /// <remarks>
    /// Value 0 is always transparent for sprites, so <paramref name="c0"/> is
    /// never drawn. Sprites choose between palette 0 and 1 through
    /// <see cref="SpriteRef.UseSecondPalette"/>.
    /// </remarks>
    [Native("gbs_set_sprite_shades")]
    public static void SetSpriteShades(byte palette, Shade c0, Shade c1, Shade c2, Shade c3) =>
        throw FrameworkOnly.Declaration();

    /// <summary>The raw background palette register, for code that wants it.</summary>
    public static byte BackgroundRaw
    {
        [Native("gbs_get_bgp")]
        get => throw FrameworkOnly.Declaration();
        [Native("gbs_set_bgp")]
        set => throw FrameworkOnly.Declaration();
    }

    /// <summary>
    /// Builds a Game Boy Color colour from three 5-bit channels.
    /// </summary>
    /// <remarks>
    /// Each channel runs 0-31, not 0-255. Folds to a constant when the arguments
    /// are literals, so a palette written inline costs nothing at runtime.
    /// </remarks>
    [Native("gbs_rgb")]
    public static ushort Rgb(byte r, byte g, byte b) => throw FrameworkOnly.Declaration();

    /// <summary>Loads Game Boy Color background palettes, four colours each.</summary>
    [Native("gbs_set_bkg_palette")]
    public static void LoadBackgroundColors(byte firstPalette, byte count, ushort[] colors) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Loads Game Boy Color sprite palettes, four colours each.</summary>
    [Native("gbs_set_sprite_palette")]
    public static void LoadSpriteColors(byte firstPalette, byte count, ushort[] colors) =>
        throw FrameworkOnly.Declaration();

    /// <summary>Restores GBDK's default colour palettes.</summary>
    [Native("set_default_palette")]
    public static void UseDefaultColors() => throw FrameworkOnly.Declaration();
}

/// <summary>
/// Indexed access to the Game Boy Color background palettes.
/// </summary>
/// <remarks>
/// Reached through <c>Hardware.BackgroundPalettes[2].SetColor(1, c)</c>, which
/// erases to a single call carrying the palette number. Holds no state.
/// </remarks>
public struct BackgroundPaletteTable
{
    /// <summary>A typed view of one palette. Lowers to <paramref name="index"/> itself.</summary>
    [NativeIdentity]
    public ref BackgroundPaletteRef this[byte index] => throw FrameworkOnly.Declaration();
}

/// <summary>One Game Boy Color background palette, by index.</summary>
public struct BackgroundPaletteRef
{
    /// <summary>Sets one of the palette's four colours.</summary>
    [Native("set_bkg_palette_entry")]
    public void SetColor(byte entry, ushort color) => throw FrameworkOnly.Declaration();
}

/// <summary>Indexed access to the Game Boy Color sprite palettes.</summary>
public struct SpritePaletteTable
{
    /// <summary>A typed view of one palette. Lowers to <paramref name="index"/> itself.</summary>
    [NativeIdentity]
    public ref SpritePaletteRef this[byte index] => throw FrameworkOnly.Declaration();
}

/// <summary>One Game Boy Color sprite palette, by index.</summary>
public struct SpritePaletteRef
{
    /// <summary>Sets one of the palette's four colours. Entry 0 is never drawn.</summary>
    [Native("set_sprite_palette_entry")]
    public void SetColor(byte entry, ushort color) => throw FrameworkOnly.Declaration();
}
