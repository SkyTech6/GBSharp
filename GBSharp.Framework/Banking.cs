using System;

namespace GB;

/// <summary>
/// Places code or read-only data outside the resident 16 KB of the cartridge.
/// </summary>
/// <remarks>
/// <para>
/// A Game Boy maps two 16 KB windows at once: bank 0, which is always present,
/// and one switchable bank. Everything without this attribute lives in bank 0,
/// which is why a program with no banking at all is capped at 32 KB.
/// </para>
/// <para>
/// The attribute answers one question (should this stay resident?), and the
/// argument answers a second, optional one: which bank. Written without a
/// number, GB# lets the linker place it and then tells you where it went, so you
/// can pin it by writing the number down. Written with one, that is where it
/// goes. Bank 0 is the resident bank, so <c>[Bank(0)]</c> on a member of a
/// banked type forces that one member to stay mapped.
/// </para>
/// <para>
/// A member's own attribute beats its containing type's, and a type's applies to
/// its methods and its <c>static readonly</c> fields. Mutable statics cannot be
/// banked: they live in work RAM, which is always mapped and is not banked on
/// this hardware.
/// </para>
/// <example>
/// <code>
/// [Bank(2)]
/// public static class ForestLevel
/// {
///     [Asset("forest.png")]
///     private static TileMap Art;
///
///     public static void Load() => Background.Load(Art);
/// }
/// </code>
/// </example>
/// <para>
/// Reaching a banked function costs more than a local call: the call goes
/// through a trampoline that saves the current bank, switches, calls and
/// switches back, and the caller's own bank is unmapped for the duration. GB#
/// reports that at the call site so nothing in a frame loop pays it by accident.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = false)]
public sealed class BankAttribute : Attribute
{
    /// <summary>Let the build choose a bank, and report which one it chose.</summary>
    public BankAttribute()
    {
        IsAutomatic = true;
    }

    /// <summary>Place this in a specific bank. 0 means the resident bank.</summary>
    public BankAttribute(int bank)
    {
        Bank = bank;
    }

    /// <summary>The requested bank, or 0 when placement is automatic.</summary>
    public int Bank { get; }

    /// <summary>True when no bank was named and the build picks one.</summary>
    public bool IsAutomatic { get; }
}

/// <summary>
/// The currently mapped ROM bank.
/// </summary>
/// <remarks>
/// <para>
/// You should not normally need this. <c>[Bank]</c> handles switching for calls
/// and the framework's loaders take the bank of the data they are given, so
/// ordinary code never names a bank at runtime.
/// </para>
/// <para>
/// It exists because the framework and your own code reach the hardware through
/// the same mechanism (thesis section 19), and because some patterns (walking a
/// table that lives in another bank, say) cannot be expressed any other way.
/// Switching by hand means you are responsible for restoring the previous bank,
/// and for not doing it from code that is itself banked: the switch would unmap
/// the function performing it.
/// </para>
/// </remarks>
public static class Banking
{
    /// <summary>The bank currently mapped into the switchable window.</summary>
    [Native("_current_bank")]
    public static byte Current => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Maps a bank into the switchable window.
    /// </summary>
    /// <remarks>
    /// Only safe from resident code. Save <see cref="Current"/> first if the
    /// caller needs the previous bank back.
    /// </remarks>
    [Native("gbs_bank_switch")]
    public static void Switch(byte bank) => throw FrameworkOnly.Declaration();
}
