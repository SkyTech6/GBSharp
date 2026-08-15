namespace GBSharp.Emulator;

/// <summary>
/// What a byte of the cartridge turned out to be.
/// </summary>
/// <remarks>
/// A byte can be both code and data: a jump table read by the code that jumps
/// through it, most obviously. The valuable state is the one with no flags at
/// all: <see cref="None"/> means nothing ever reached that byte, which is how a
/// ROM says which of its code a play session never ran.
/// </remarks>
[Flags]
public enum RomUsage : byte
{
    /// <summary>Never reached, as code or as data.</summary>
    None = 0,

    /// <summary>Executed, or part of an instruction that was.</summary>
    Code = 0x1,

    /// <summary>Read as data.</summary>
    Data = 0x2,

    /// <summary>
    /// The first byte of an executed instruction.
    /// </summary>
    /// <remarks>
    /// Counting these rather than <see cref="Code"/> bytes counts instructions
    /// rather than bytes, which is the fairer measure of how much of a function
    /// ran: a three-byte instruction is no more executed than a one-byte one.
    /// </remarks>
    CodeStart = 0x4,
}
