namespace GBSharp.Emulator;

/// <summary>
/// Why a run stopped.
/// </summary>
/// <remarks>
/// More than one flag can be set: a frame that completes exactly as its tick
/// deadline expires reports both. Mirrors <c>gbsharp_event</c>, which the
/// runtime checks against the core's own values at compile time.
/// </remarks>
[Flags]
public enum EmulatorEvent : uint
{
    /// <summary>Nothing happened, which is what a call with no emulator reports.</summary>
    None = 0,

    /// <summary>The PPU finished a frame, so the framebuffer holds a whole one.</summary>
    NewFrame = 0x1,

    /// <summary>The audio buffer filled. Drain it or it is discarded.</summary>
    AudioBufferFull = 0x2,

    /// <summary>The tick deadline was reached.</summary>
    UntilTicks = 0x4,

    /// <summary>
    /// Execution stopped at a breakpoint, before the instruction there ran.
    /// </summary>
    /// <remarks>
    /// The frame is unfinished when this is set, so the framebuffer holds part
    /// of one. Resuming continues the same frame rather than starting the next.
    /// </remarks>
    Breakpoint = 0x8,

    /// <summary>The CPU met an opcode that is not an instruction.</summary>
    InvalidOpcode = 0x10,
}
