using System.Collections;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Diagnostics;

/// <summary>
/// Collects diagnostics through one compilation.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<GBDiagnostic>
{
    private readonly List<GBDiagnostic> _diagnostics = [];
    private readonly GBDiagnosticOptions _options;

    public DiagnosticBag()
        : this(GBDiagnosticOptions.Default)
    {
    }

    public DiagnosticBag(GBDiagnosticOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<GBDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(d => d.IsError);

    public int Count => _diagnostics.Count;

    /// <summary>Reports a diagnostic at a Roslyn source location.</summary>
    public GBDiagnostic? Report(GBDiagnosticDescriptor descriptor, Location? location, params object?[] args) =>
        Report(descriptor, SourceSpan.FromLocation(location), args);

    /// <summary>
    /// Reports every configuration request that could not be honoured.
    /// </summary>
    /// <remarks>
    /// Called once per compilation. Declining silently would leave a developer
    /// believing they had turned something off.
    /// </remarks>
    public void ReportRefusedSuppressions(IEnumerable<GBDiagnosticDescriptor> allDescriptors)
    {
        foreach (GBDiagnosticDescriptor descriptor in allDescriptors)
        {
            if (_options.IsRefused(descriptor))
            {
                Report(GBDiagnostics.DiagnosticNotSuppressible, SourceSpan.None, descriptor.Id);
            }
        }
    }

    /// <summary>
    /// Reports a diagnostic at an already-resolved span, or drops it if it has
    /// been suppressed.
    /// </summary>
    /// <returns>
    /// The diagnostic, or null when configuration silenced it. Callers that use
    /// the return value are reporting something they then describe further; those
    /// that ignore it are unaffected.
    /// </returns>
    public GBDiagnostic? Report(GBDiagnosticDescriptor descriptor, SourceSpan span, params object?[] args)
    {
        GBSeverity? severity = _options.SeverityFor(descriptor);
        if (severity is null)
        {
            return null;
        }

        string message = args.Length == 0
            ? descriptor.MessageFormat
            : string.Format(descriptor.MessageFormat, args);

        var diagnostic = new GBDiagnostic(descriptor, message, span, severity.Value);
        _diagnostics.Add(diagnostic);
        return diagnostic;
    }

    /// <summary>
    /// Adopts a Roslyn diagnostic unchanged.
    /// </summary>
    /// <remarks>
    /// Used only for C# errors that GB# has nothing to add to. If the code is
    /// not valid C#, saying so in Roslyn's own words is the clearest answer.
    /// </remarks>
    public void ReportRoslyn(Diagnostic diagnostic)
    {
        GBSeverity severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => GBSeverity.Error,
            DiagnosticSeverity.Warning => GBSeverity.Warning,
            _ => GBSeverity.Info,
        };

        var descriptor = new GBDiagnosticDescriptor(
            diagnostic.Id,
            diagnostic.Descriptor.Title.ToString(),
            "{0}",
            GBDiagnosticCategory.Language,
            severity);

        _diagnostics.Add(new GBDiagnostic(
            descriptor,
            diagnostic.GetMessage(),
            SourceSpan.FromLocation(diagnostic.Location),
            severity));
    }

    public void AddRange(IEnumerable<GBDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    public IEnumerator<GBDiagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
