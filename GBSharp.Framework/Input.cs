namespace GB;

/// <summary>
/// The joypad, sampled at the point of use.
/// </summary>
/// <remarks>
/// Each property reads the joypad register directly, so testing several buttons
/// costs several reads. Call <see cref="Read"/> once and test the result with
/// <see cref="Button"/> when that matters.
/// </remarks>
public static class Input
{
    [Native("gbs_input_right")]
    public static bool Right => throw FrameworkOnly.Declaration();

    [Native("gbs_input_left")]
    public static bool Left => throw FrameworkOnly.Declaration();

    [Native("gbs_input_up")]
    public static bool Up => throw FrameworkOnly.Declaration();

    [Native("gbs_input_down")]
    public static bool Down => throw FrameworkOnly.Declaration();

    [Native("gbs_input_a")]
    public static bool A => throw FrameworkOnly.Declaration();

    [Native("gbs_input_b")]
    public static bool B => throw FrameworkOnly.Declaration();

    [Native("gbs_input_start")]
    public static bool Start => throw FrameworkOnly.Declaration();

    [Native("gbs_input_select")]
    public static bool Select => throw FrameworkOnly.Declaration();

    /// <summary>Samples every button once, returning a <see cref="Button"/> mask.</summary>
    [Native("joypad")]
    public static byte Read() => throw FrameworkOnly.Declaration();
}

/// <summary>
/// Joypad bit masks, matching GBDK's <c>J_*</c> constants.
/// </summary>
public enum Button : byte
{
    Right = 0x01,
    Left = 0x02,
    Up = 0x04,
    Down = 0x08,
    A = 0x10,
    B = 0x20,
    Select = 0x40,
    Start = 0x80,
}
