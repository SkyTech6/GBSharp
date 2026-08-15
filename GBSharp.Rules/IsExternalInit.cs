// 'record' and 'init' accessors need this type to exist. The runtime provides it
// from .NET 5 onwards; netstandard2.0 does not, and the compiler is satisfied by
// any definition it can see. Internal, so it never leaks out of this assembly.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
