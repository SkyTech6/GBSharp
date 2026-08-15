using System.Text.Json.Serialization;
using GBSharp.Compiler.IR;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>
/// One emitted C function traced back to the C# method that produced it.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the symbol chain that no other artefact records. The
/// linker's <c>.sym</c> gets an address to a C name, and <c>sourcemap.json</c>
/// gets a C# line to a C line, but nothing joins a C <em>function</em> to the
/// method it was lowered from, and a running program counter lands inside a
/// function, not on a statement.
/// </para>
/// <para>
/// Written on every build rather than only under <c>--annotate-source</c>,
/// because a chain that works on some builds and silently stops on others is
/// worse than no chain: the failure looks like a missing symbol rather than a
/// missing flag.
/// </para>
/// </remarks>
/// <param name="Name">The C function name, as it appears in the <c>.sym</c> without its underscore.</param>
/// <param name="Method">The C# method, or <see langword="null"/> for one GB# synthesised.</param>
/// <param name="File">The C# source file, or <see langword="null"/> when there is none.</param>
/// <param name="Line">The 1-based line of the declaration, or 0 when there is none.</param>
public sealed record FunctionMapEntry(string Name, string? Method, string? File, int Line)
{
    /// <summary>
    /// The name a <see cref="File"/> path ends in, regardless of which OS built it.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> only treats <c>\</c> as a separator
    /// on Windows, so a ROM built there and read back on Linux or macOS would
    /// otherwise keep the whole path instead of just the file name.
    /// </remarks>
    public static string ShortFileName(string path) =>
        path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>
    /// Every function in a module, in the order it was lowered.
    /// </summary>
    /// <remarks>
    /// Compiler-generated functions are included with no method or file. Naming
    /// <c>gbs_list_add</c> and saying GB# wrote it answers "what is running"
    /// better than an empty result does, and leaving them out would make a
    /// program counter inside one indistinguishable from an unresolvable one.
    /// </remarks>
    public static FunctionMapEntry[] From(IRModule module) =>
        [.. module.Functions.Select(function => new FunctionMapEntry(
            function.Name,
            function.SourceName,
            function.Span.IsNone ? null : function.Span.FilePath,
            function.Span.IsNone ? 0 : function.Span.Line))];
}

/// <summary>
/// Source-generated serialisation for <c>&lt;rom&gt;.functions.json</c>, the same
/// approach the source map and the build report already take.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FunctionMapEntry[]))]
internal sealed partial class FunctionMapJson : JsonSerializerContext
{
}
