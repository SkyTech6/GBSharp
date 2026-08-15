using System.Runtime.InteropServices;

namespace GBSharp.Emulator;

/// <summary>
/// The CPU's registers at one instant.
/// </summary>
/// <remarks>
/// <para>
/// Laid out to match <c>gbsharp_registers</c> field for field, so it crosses
/// the boundary as a blittable struct rather than through marshalling. The ABI
/// promises the layout: fields are appended, never reordered or removed, and
/// appending bumps the ABI version like anything else.
/// </para>
/// <para>
/// Read as a set rather than one register at a time, because a caller reading
/// them one at a time could see a torn set, and because a register dump is
/// what a caller actually wants.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct CpuRegisters
{
    /// <summary>The accumulator and flags as one 16-bit register.</summary>
    public ushort AF { get; init; }

    public ushort BC { get; init; }

    public ushort DE { get; init; }

    public ushort HL { get; init; }

    /// <summary>The stack pointer.</summary>
    public ushort SP { get; init; }

    /// <summary>The program counter: the address about to execute.</summary>
    public ushort PC { get; init; }

    /// <summary>The accumulator.</summary>
    public byte A { get; init; }

    /// <summary>
    /// The flags, packed as the hardware packs them: Z, N, H then C in bits 7
    /// down to 4, low nibble always zero.
    /// </summary>
    /// <remarks>
    /// Packed rather than four booleans so it can be compared against a byte
    /// read out of a stack frame, which is where flags are usually met.
    /// </remarks>
    public byte F { get; init; }

    /// <summary>Zero flag: the last result was zero.</summary>
    public bool Zero => (F & 0x80) != 0;

    /// <summary>Subtract flag, which only BCD correction reads.</summary>
    public bool Subtract => (F & 0x40) != 0;

    /// <summary>Half-carry flag, likewise.</summary>
    public bool HalfCarry => (F & 0x20) != 0;

    /// <summary>Carry flag.</summary>
    public bool Carry => (F & 0x10) != 0;

    public override string ToString() =>
        $"PC:{PC:X4} SP:{SP:X4} A:{A:X2} F:{(Zero ? 'Z' : '-')}{(Subtract ? 'N' : '-')}" +
        $"{(HalfCarry ? 'H' : '-')}{(Carry ? 'C' : '-')} BC:{BC:X4} DE:{DE:X4} HL:{HL:X4}";
}
