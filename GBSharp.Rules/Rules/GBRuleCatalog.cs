using GBSharp.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;

namespace GBSharp.Rules;

/// <summary>
/// Which diagnostics the analyzers are responsible for, and how a GB# descriptor
/// becomes a Roslyn one.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer's <c>SupportedDiagnostics</c> is derived from
/// <see cref="IdeReportable"/> rather than written out by hand, so a rule cannot
/// be implemented without being declared or declared without being implemented.
/// The parity test then asserts the harder half: that the ids the analyzer
/// reports are a subset of what a full build reports, never a superset.
/// </para>
/// <para>
/// Everything here is answerable from symbols and operations alone. Nothing that
/// needs the lowerer's width inference, its known-function set, or a file on
/// disk belongs in this list: an analyzer runs on every keystroke.
/// </para>
/// <para>
/// The <see cref="GBDiagnosticCategory.CycleCost"/> band is the clearest example
/// of what stays out. Every one of its diagnostics needs the whole lowered
/// module (a call graph keyed on mangled names, resolved banks, inferred widths),
/// and an analyzer sees one syntax tree. Reporting them here would produce
/// squiggles a build could neither reproduce nor explain, which is exactly what
/// the parity test forbids.
/// </para>
/// </remarks>
public static class GBRuleCatalog
{
    /// <summary>
    /// The diagnostics an analyzer reports in the editor, ahead of any build.
    /// </summary>
    public static readonly GBDiagnosticDescriptor[] IdeReportable =
    [
        GBDiagnostics.UnsupportedType,
        GBDiagnostics.DynamicCollection,
        GBDiagnostics.StringType,
        GBDiagnostics.Exceptions,
        GBDiagnostics.DelegatesAndEvents,
        GBDiagnostics.Interfaces,
        GBDiagnostics.AsyncAwait,
        GBDiagnostics.Linq,
        GBDiagnostics.ReferenceTypeAllocation,
        GBDiagnostics.Int32Arithmetic,
        GBDiagnostics.StaticAllocation,
        GBDiagnostics.RomAllocation,
    ];

    /// <summary>
    /// The Roslyn form of a GB# descriptor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roslyn's severity scale has four values and no notion of "this is what it
    /// costs". Performance and Resource therefore arrive as
    /// <see cref="DiagnosticSeverity.Info"/> and carry the distinction in their
    /// category instead, which is the axis .editorconfig can actually configure:
    /// <c>dotnet_analyzer_diagnostic.category-GBSharp.Memory.severity = none</c>
    /// silences every resource note without touching a real error.
    /// </para>
    /// <para>
    /// They must not become warnings. Both are always-on and unfixable by
    /// design: GBS0201 fires on every static field a program declares, and a
    /// project built with warnings-as-errors would stop compiling.
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor ToRoslyn(GBDiagnosticDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.Title,
        descriptor.MessageFormat,
        "GBSharp." + descriptor.Category,
        Severity(descriptor.DefaultSeverity),
        isEnabledByDefault: true,
        description: descriptor.Help);

    private static DiagnosticSeverity Severity(GBSeverity severity) => severity switch
    {
        GBSeverity.Error => DiagnosticSeverity.Error,
        GBSeverity.Warning => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };
}
