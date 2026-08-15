using System;

namespace GB;

/// <summary>
/// Marks a member that exists only to give a value a name and a type, and that
/// lowers to its own argument rather than to a call.
/// </summary>
/// <remarks>
/// <para>
/// This is how GB# offers typed views over hardware indices without paying for
/// them. <c>Sprites[0]</c> is a <see cref="NativeIdentityAttribute"/> indexer:
/// it lowers to the constant <c>0</c>, and the <see cref="SpriteRef"/> it
/// "returns" exists only in the type system. The hardware access happens in the
/// member accessed through it.
/// </para>
/// <para>
/// A member marked this way must take exactly one value through (the single
/// argument, or the receiver for a parameterless instance member).
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = false)]
public sealed class NativeIdentityAttribute : Attribute
{
}
