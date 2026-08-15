using System;

namespace GB;

/// <summary>
/// The most work RAM this game may use, in bytes.
/// </summary>
/// <remarks>
/// <para>
/// Exceeding it fails the build. That is the point: a budget nobody enforces is
/// a comment, and the whole value of declaring one is that it holds while nobody
/// is looking (thesis section 26).
/// </para>
/// <para>
/// Checked against what the linker actually placed, not what the code declared.
/// The real figure includes the stack, shadow OAM and GBDK's own state, so a
/// declared-bytes total would let a game creep past its budget and still pass.
/// </para>
/// <example>
/// <code>
/// [assembly: MaxWRAM(6144)]
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class MaxWRAMAttribute : Attribute
{
    public MaxWRAMAttribute(int bytes) => Bytes = bytes;

    public int Bytes { get; }
}

/// <summary>The largest ROM image this game may produce, in bytes.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class MaxROMAttribute : Attribute
{
    public MaxROMAttribute(int bytes) => Bytes = bytes;

    public int Bytes { get; }
}

/// <summary>
/// The most 16 KB ROM banks this game may occupy, counting bank 0.
/// </summary>
/// <remarks>
/// Useful where the cartridge size is a cost rather than a limit: a smaller
/// mapper, or a flash cart with a fixed budget.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class MaxROMBanksAttribute : Attribute
{
    public MaxROMBanksAttribute(int banks) => Banks = banks;

    public int Banks { get; }
}
