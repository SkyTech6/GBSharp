using System;

namespace GB;

/// <summary>
/// Shared failure for framework members that exist only as declarations.
/// </summary>
/// <remarks>
/// Members of the GB# framework are compile-time contracts. Their bodies are
/// never lowered to the target and never execute on the host; reaching one at
/// runtime means the assembly was used as an ordinary .NET library rather than
/// compiled by <c>gbsharp</c>.
/// </remarks>
internal static class FrameworkOnly
{
    internal static NotSupportedException Declaration([System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        return new NotSupportedException(
            $"'{member}' is a GB# compile-time declaration and has no host implementation. " +
            "Compile this project with 'gbsharp build'.");
    }
}
