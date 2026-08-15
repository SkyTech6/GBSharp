using System.Runtime.InteropServices;

namespace GBSharp.Emulator;

/// <summary>
/// Owns a <c>gbsharp_emulator*</c>.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a raw <see cref="nint"/> so that the
/// interop marshaller keeps the handle alive for the duration of every call.
/// A test that disposes a <see cref="GameBoy"/> while a frame is running on
/// another thread would otherwise be freeing memory the emulator is inside of.
/// </remarks>
public sealed class GameBoyHandle : SafeHandle
{
    /// <summary>
    /// Required by the interop source generator, which constructs the handle
    /// before it has a value to put in it.
    /// </summary>
    public GameBoyHandle()
        : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.Destroy(handle);
        return true;
    }
}
