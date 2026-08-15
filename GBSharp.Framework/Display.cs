namespace GB;

/// <summary>The LCD controller.</summary>
public static class Display
{
    /// <summary>Turns the LCD on.</summary>
    [Native("gbs_display_on")]
    public static void Enable() => throw FrameworkOnly.Declaration();

    /// <summary>Turns the LCD off. Only safe during VBlank.</summary>
    [Native("gbs_display_off")]
    public static void Disable() => throw FrameworkOnly.Declaration();

    /// <summary>Makes the sprite layer visible.</summary>
    [Native("gbs_show_sprites")]
    public static void ShowSprites() => throw FrameworkOnly.Declaration();

    /// <summary>Makes the background layer visible.</summary>
    [Native("gbs_show_background")]
    public static void ShowBackground() => throw FrameworkOnly.Declaration();

    /// <summary>Hides the sprite layer.</summary>
    [Native("gbs_hide_sprites")]
    public static void HideSprites() => throw FrameworkOnly.Declaration();

    /// <summary>Hides the background layer.</summary>
    [Native("gbs_hide_background")]
    public static void HideBackground() => throw FrameworkOnly.Declaration();

    /// <summary>Makes the window layer visible.</summary>
    [Native("gbs_show_window")]
    public static void ShowWindow() => throw FrameworkOnly.Declaration();

    /// <summary>Hides the window layer.</summary>
    [Native("gbs_hide_window")]
    public static void HideWindow() => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Switches every sprite to 8x16.
    /// </summary>
    /// <remarks>
    /// This is a global mode, not a per-sprite one. In 8x16 mode a sprite's tile
    /// index has its low bit ignored, so tile 5 and tile 4 name the same pair.
    /// </remarks>
    [Native("gbs_sprites_8x16")]
    public static void UseTallSprites() => throw FrameworkOnly.Declaration();

    /// <summary>Switches every sprite back to 8x8.</summary>
    [Native("gbs_sprites_8x8")]
    public static void UseShortSprites() => throw FrameworkOnly.Declaration();
}
