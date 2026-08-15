namespace GBSharp.Sdk;

/// <summary>
/// Nothing uses this.
/// </summary>
/// <remarks>
/// This project ships MSBuild props and targets, not code, but it is built by
/// Microsoft.NET.Sdk, which expects something to compile. The resulting assembly
/// is excluded from the package by <c>IncludeBuildOutput=false</c>; only the
/// <c>Sdk/</c> folder is packed. A type here is cheaper than a second SDK
/// dependency to avoid one.
/// </remarks>
internal static class SdkMarker
{
}
