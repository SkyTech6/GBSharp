namespace GB;

/// <summary>Frame timing and the machine itself.</summary>
public static class Game
{
    /// <summary>
    /// Blocks until the next VBlank. This is the frame boundary: OAM and VRAM
    /// are only safe to touch immediately after it returns.
    /// </summary>
    [Native("gbs_wait_vblank")]
    public static void WaitVBlank() => throw FrameworkOnly.Declaration();

    /// <summary>Halts the CPU until the next interrupt.</summary>
    [Native("gbs_halt")]
    public static void Halt() => throw FrameworkOnly.Declaration();
}
