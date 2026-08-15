using System;

namespace GB;

/// <summary>
/// Maps a GB# member onto a C symbol in the target backend.
/// </summary>
/// <remarks>
/// <para>
/// This is the single mechanism by which GB# code reaches the underlying
/// platform. The framework in this assembly uses it, and so does user code that
/// needs to call a GBDK function the framework does not wrap (thesis section 19).
/// </para>
/// <para>
/// A member marked <see cref="NativeAttribute"/> is never lowered. The compiler
/// discards its body and emits a direct call to <see cref="Symbol"/>, so the
/// declared signature must match the C function it names.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
public sealed class NativeAttribute : Attribute
{
    public NativeAttribute(string symbol)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
    }

    /// <summary>The C identifier to emit at the call site.</summary>
    public string Symbol { get; }
}
