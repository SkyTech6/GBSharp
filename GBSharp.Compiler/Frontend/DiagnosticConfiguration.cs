using GBSharp.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace GBSharp.Compiler.Frontend;

/// <summary>
/// Builds the severity overrides for a compilation, by id and by category.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, in precedence order: the project file, then
/// <c>.editorconfig</c>. The project file wins because it is the more specific
/// statement: a developer who wrote a setting for this game meant it for this
/// game, whereas an <c>.editorconfig</c> may have been inherited from a
/// directory above.
/// </para>
/// <para>
/// Both sources speak both scales. An id is
/// <c>dotnet_diagnostic.GBS0201.severity</c> or <c>"GBS0201"</c>; a category is
/// <c>dotnet_analyzer_diagnostic.category-GBSharp.Memory.severity</c> or
/// <c>"GBSharp.Memory"</c>. Precedence between the two scales is resolved in
/// <see cref="GBDiagnosticOptions.SeverityFor"/>, not here; this class only
/// collects what was written.
/// </para>
/// <para>
/// The <c>.editorconfig</c> is read with Roslyn's own
/// <see cref="AnalyzerConfigSet"/> rather than a hand-rolled INI parser. Nesting,
/// globs and precedence are all subtle and all already implemented; GB# already
/// references Roslyn, so re-implementing them would be inventing a second set of
/// rules for a file format developers already know.
/// </para>
/// </remarks>
public static class DiagnosticConfiguration
{
    /// <summary>
    /// Reads severity overrides for the ids GB# knows about.
    /// </summary>
    /// <param name="projectSettings">
    /// Id to severity spelling, from the project file. Wins over the config files.
    /// </param>
    /// <param name="configPaths">
    /// <c>.editorconfig</c> files to consider, nearest last.
    /// </param>
    /// <param name="sourcePath">
    /// A source file the settings should apply to, used to resolve globs.
    /// </param>
    public static GBDiagnosticOptions Read(
        IReadOnlyDictionary<string, string>? projectSettings,
        IReadOnlyList<string> configPaths,
        string? sourcePath)
    {
        var overrides = new Dictionary<string, GBSeverity?>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<GBDiagnosticCategory, GBSeverity?>();

        ReadEditorConfig(overrides, categories, configPaths, sourcePath);

        // Applied second so it wins.
        if (projectSettings is not null)
        {
            foreach ((string key, string value) in projectSettings)
            {
                if (!GBDiagnosticOptions.TryParseSeverity(value, out GBSeverity? severity))
                {
                    continue;
                }

                // A category is tried first because an id is the narrower shape:
                // nothing that parses as a category could also be an id.
                if (GBDiagnosticOptions.TryParseCategory(key, out GBDiagnosticCategory category))
                {
                    categories[category] = severity;
                }
                else
                {
                    overrides[key] = severity;
                }
            }
        }

        return overrides.Count == 0 && categories.Count == 0
            ? GBDiagnosticOptions.Default
            : new GBDiagnosticOptions(overrides, categories);
    }

    private static void ReadEditorConfig(
        Dictionary<string, GBSeverity?> overrides,
        Dictionary<GBDiagnosticCategory, GBSeverity?> categories,
        IReadOnlyList<string> configPaths,
        string? sourcePath)
    {
        if (configPaths.Count == 0 || sourcePath is null)
        {
            return;
        }

        var configs = new List<AnalyzerConfig>();

        foreach (string path in configPaths)
        {
            try
            {
                configs.Add(AnalyzerConfig.Parse(File.ReadAllText(path), path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable .editorconfig is not worth failing a build over;
                // the effect is that its settings do not apply.
            }
        }

        if (configs.Count == 0)
        {
            return;
        }

        AnalyzerConfigOptionsResult options = AnalyzerConfigSet
            .Create(configs)
            .GetOptionsForSourcePath(sourcePath);

        // Roslyn recognises dotnet_diagnostic.<id>.severity itself and hands it
        // back already parsed, in TreeOptions rather than among the free-form
        // analyzer options. Reading it from there means GB# ids are configured by
        // exactly the mechanism that configures CS and IDE ones, down to the
        // spellings that count as valid.
        foreach ((string id, ReportDiagnostic report) in options.TreeOptions)
        {
            if (!id.StartsWith("GBS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryMapReport(report, out GBSeverity? severity))
            {
                overrides[id.ToUpperInvariant()] = severity;
            }
        }

        ReadCategoryOptions(categories, options);
    }

    /// <summary>
    /// The <c>dotnet_analyzer_diagnostic.category-GBSharp.&lt;Category&gt;.severity</c>
    /// form, which configures a whole band at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roslyn parses <c>dotnet_diagnostic.&lt;id&gt;.severity</c> itself and hands
    /// it back in <c>TreeOptions</c>, but it leaves the category form among the
    /// free-form analyzer options, because only an analyzer host knows what
    /// categories exist. So this reads and maps it by hand.
    /// </para>
    /// <para>
    /// Until this existed, the category form worked in the IDE (where Roslyn
    /// applies it to the analyzer's own descriptors) and silently did nothing in
    /// a <c>gbsharp build</c>, which is the worse half of the two to get wrong: a
    /// developer who mutes a band and still sees it can at least tell something is
    /// broken.
    /// </para>
    /// </remarks>
    private static void ReadCategoryOptions(
        Dictionary<GBDiagnosticCategory, GBSeverity?> categories,
        AnalyzerConfigOptionsResult options)
    {
        const string prefix = "dotnet_analyzer_diagnostic.category-";
        const string suffix = ".severity";

        foreach ((string key, string value) in options.AnalyzerOptions)
        {
            // Roslyn lowercases option keys, so every comparison here is ordinal
            // and case-insensitive rather than trusting the developer's spelling.
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);

            if (!name.StartsWith(GBDiagnosticOptions.CategoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Some other analyzer's category. Not ours to interpret.
                continue;
            }

            if (GBDiagnosticOptions.TryParseCategory(name, out GBDiagnosticCategory category)
                && GBDiagnosticOptions.TryParseSeverity(value, out GBSeverity? severity))
            {
                categories[category] = severity;
            }
        }
    }

    /// <summary>
    /// Roslyn's severity scale onto GB#'s.
    /// </summary>
    /// <remarks>
    /// <c>Default</c> means "no opinion", so it is not an override at all.
    /// <c>Hidden</c> means shown nowhere, which for a command-line build is
    /// indistinguishable from suppressed.
    /// </remarks>
    private static bool TryMapReport(ReportDiagnostic report, out GBSeverity? severity)
    {
        switch (report)
        {
            case ReportDiagnostic.Suppress:
            case ReportDiagnostic.Hidden:
                severity = null;
                return true;

            case ReportDiagnostic.Error:
                severity = GBSeverity.Error;
                return true;

            case ReportDiagnostic.Warn:
                severity = GBSeverity.Warning;
                return true;

            case ReportDiagnostic.Info:
                severity = GBSeverity.Info;
                return true;

            default:
                severity = null;
                return false;
        }
    }

    /// <summary>
    /// Every <c>.editorconfig</c> from the project directory up to the root,
    /// outermost first so the nearest one wins.
    /// </summary>
    public static IReadOnlyList<string> FindConfigFiles(string directory)
    {
        var found = new List<string>();

        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, ".editorconfig");
            if (File.Exists(candidate))
            {
                found.Add(candidate);
            }
        }

        found.Reverse();
        return found;
    }
}
