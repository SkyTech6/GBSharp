using System.Text.Json.Serialization;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// One generated C statement traced back to the C# line that produced it.
/// </summary>
/// <remarks>
/// Produced only when <c>--annotate-source</c> is on, alongside the same
/// comments <see cref="CEmitter"/> writes inline: this is that information in
/// a form a tool can read without parsing comments out of C.
/// </remarks>
/// <param name="File">The C# source file, as GB# saw it.</param>
/// <param name="Line">The 1-based line in <see cref="File"/>.</param>
/// <param name="GeneratedFile">The bare name of the emitted C file, e.g. <c>game.c</c>.</param>
/// <param name="GeneratedLine">The 1-based line in <see cref="GeneratedFile"/>.</param>
public sealed record SourceMapEntry(string File, int Line, string GeneratedFile, int GeneratedLine);

/// <summary>
/// Source-generated serialisation for <c>build/c/sourcemap.json</c>, so no
/// reflection and no new package, the same approach as the build report.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceMapEntry[]))]
internal sealed partial class SourceMapJson : JsonSerializerContext
{
}
