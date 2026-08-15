using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace GBSharp.Compiler.Diagnostics;

/// <summary>
/// Diagnostic severities (thesis section 7).
/// </summary>
/// <remarks>
/// <see cref="Performance"/> and <see cref="Resource"/> are distinct from
/// <see cref="Warning"/> because they say something about the hardware rather
/// than about the code's correctness. Developers filter on that distinction.
/// </remarks>
public enum GBSeverity
{
    /// <summary>Compilation cannot continue.</summary>
    Error,

    /// <summary>The code is suspect.</summary>
    Warning,

    /// <summary>The code is correct but costs more than it looks like it does.</summary>
    Performance,

    /// <summary>The code consumes a constrained resource: WRAM, VRAM, ROM, sprites.</summary>
    Resource,

    /// <summary>Informational only.</summary>
    Info,
}

/// <summary>
/// The immutable definition of one GB# diagnostic.
/// </summary>
/// <remarks>
/// Every diagnostic GB# reports is declared once in
/// <see cref="GBDiagnostics"/>. Nothing constructs an ad hoc message, because a
/// diagnostic without an id is a diagnostic nobody can suppress, search for, or
/// document.
/// </remarks>
public sealed class GBDiagnosticDescriptor
{
    public GBDiagnosticDescriptor(
        string id,
        string title,
        string messageFormat,
        GBDiagnosticCategory category,
        GBSeverity defaultSeverity,
        string? help = null,
        bool isSuppressible = false)
    {
        Id = id;
        Title = title;
        MessageFormat = messageFormat;
        Category = category;
        DefaultSeverity = defaultSeverity;
        Help = help;
        IsSuppressible = isSuppressible;
    }

    /// <summary>The stable identifier, e.g. <c>GBS0042</c>.</summary>
    public string Id { get; }

    /// <summary>A short noun phrase naming the problem.</summary>
    public string Title { get; }

    /// <summary>A composite format string filled in at report time.</summary>
    public string MessageFormat { get; }

    public GBDiagnosticCategory Category { get; }

    public GBSeverity DefaultSeverity { get; }

    /// <summary>
    /// What the developer should do instead. This is the part that teaches, so
    /// most descriptors should have one.
    /// </summary>
    public string? Help { get; }

    /// <summary>
    /// Whether a developer may silence or downgrade this.
    /// </summary>
    /// <remarks>
    /// False for anything the compiler depends on stopping the build. Lowering
    /// answers "I cannot represent this" by returning null, and the pipeline
    /// relies on <c>HasErrors</c> to stop before that null is used, so
    /// downgrading GBS0042 to a warning would not produce a program with a
    /// <c>List</c> in it, it would produce nonsense C. Costs and resource notes
    /// are freely suppressible: they describe the hardware, and nothing downstream
    /// reads them.
    /// </remarks>
    public bool IsSuppressible { get; }

    public override string ToString() => $"{Id}: {Title}";
}

/// <summary>
/// Diagnostic id ranges. The range a diagnostic falls in is part of its
/// contract, so new diagnostics must be allocated within the right band.
/// </summary>
public enum GBDiagnosticCategory
{
    /// <summary>GBS0001-GBS0099: constructs outside the GB# language subset.</summary>
    Language,

    /// <summary>GBS0100-GBS0199: operations that are expensive on SM83.</summary>
    Performance,

    /// <summary>GBS0200-GBS0299: WRAM, VRAM and ROM consumption.</summary>
    Memory,

    /// <summary>GBS0300-GBS0399: ROM banking.</summary>
    Banking,

    /// <summary>GBS0400-GBS0499: estimated cycle costs.</summary>
    CycleCost,

    /// <summary>GBS0500-GBS0599: the toolchain and the build itself.</summary>
    Toolchain,

    /// <summary>GBS0600-GBS0699: the asset pipeline.</summary>
    Assets,

    /// <summary>GBS9000+: bugs in GB# itself.</summary>
    Internal,
}

/// <summary>
/// A position in the developer's C# source.
/// </summary>
/// <remarks>
/// GB# diagnostics always point at C#, never at generated C. A developer who
/// has to read generated C to understand an error has been failed by the
/// compiler (thesis section 20).
/// </remarks>
public sealed record SourceSpan(string FilePath, int Line, int Column, int Length)
{
    /// <summary>A span for something with no meaningful source position.</summary>
    public static SourceSpan None { get; } = new("<none>", 0, 0, 0);

    public static SourceSpan FromLocation(Location? location)
    {
        if (location is null || location.Kind != LocationKind.SourceFile)
        {
            return None;
        }

        FileLinePositionSpan mapped = location.GetLineSpan();
        LinePosition start = mapped.StartLinePosition;

        return new SourceSpan(
            mapped.Path,
            start.Line + 1,       // Roslyn is 0-based; humans and editors are 1-based.
            start.Character + 1,
            location.SourceSpan.Length);
    }

    public bool IsNone => ReferenceEquals(this, None) || Line == 0;

    public override string ToString() => IsNone ? "<none>" : $"{FilePath}({Line},{Column})";
}

/// <summary>One reported diagnostic: a descriptor, a filled-in message, a place.</summary>
public sealed record GBDiagnostic(
    GBDiagnosticDescriptor Descriptor,
    string Message,
    SourceSpan Span,
    GBSeverity Severity)
{
    public string Id => Descriptor.Id;

    public bool IsError => Severity == GBSeverity.Error;

    public override string ToString() => $"{Span}: {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}
