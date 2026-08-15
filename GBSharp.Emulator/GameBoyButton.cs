namespace GBSharp.Emulator;

/// <summary>
/// A joypad button, as <c>gbsharp_button</c> numbers them.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="GB.Button"/>, which is a bit mask matching
/// GBDK's <c>J_*</c> constants and belongs to code running on the Game Boy.
/// This is the ABI's own numbering, and nothing here may drift from
/// <c>gbsharp.h</c> without the ABI version being bumped.
/// </remarks>
public enum GameBoyButton
{
    Right = 0,
    Left = 1,
    Up = 2,
    Down = 3,
    A = 4,
    B = 5,
    Select = 6,
    Start = 7,
}
