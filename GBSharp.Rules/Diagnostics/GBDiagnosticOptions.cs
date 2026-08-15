namespace GBSharp.Compiler.Diagnostics;

/// <summary>
/// Severity overrides for one compilation, by id and by category.
/// </summary>
/// <remarks>
/// <para>
/// A diagnostic nobody can silence is a diagnostic that eventually gets ignored
/// wholesale. GBS0201 fires on every static field a program declares, and a
/// developer who has accepted their WRAM budget needs a way to stop hearing about
/// it without also stopping hearing about the next thing.
/// </para>
/// <para>
/// Categories exist because bands arrive whole. A developer who does not want
/// estimated cycle costs does not want any of them, and configuring that one id
/// at a time means editing the setting again every time GB# learns to report
/// something new. An id beats a category for the same reason the project file
/// beats an <c>.editorconfig</c>: the more specific statement wins.
/// </para>
/// <para>
/// What cannot be silenced is anything the build depends on stopping for; see
/// <see cref="GBDiagnosticDescriptor.IsSuppressible"/>. Naming one of those by id
/// is reported rather than quietly ignored, because silently declining to obey a
/// setting is worse than refusing it. Naming its <em>category</em> is not
/// reported: a blanket statement about a band is not a claim about any particular
/// member of it, and refusing it would mean a developer muting a category could
/// be scolded for a descriptor they have never heard of.
/// </para>
/// </remarks>
public sealed class GBDiagnosticOptions
{
    /// <summary>No overrides: every descriptor keeps its declared severity.</summary>
    public static GBDiagnosticOptions Default { get; } = new(new Dictionary<string, GBSeverity?>(0));

    /// <summary>
    /// The prefix a category carries in configuration, matching the Roslyn
    /// category <c>GBRuleCatalog.ToRoslyn</c> assigns.
    /// </summary>
    public const string CategoryPrefix = "GBSharp.";

    private readonly IReadOnlyDictionary<string, GBSeverity?> _overrides;
    private readonly IReadOnlyDictionary<GBDiagnosticCategory, GBSeverity?> _categoryOverrides;

    /// <param name="overrides">
    /// Id to severity. A null value means suppressed entirely.
    /// </param>
    public GBDiagnosticOptions(IReadOnlyDictionary<string, GBSeverity?> overrides)
        : this(overrides, new Dictionary<GBDiagnosticCategory, GBSeverity?>(0))
    {
    }

    /// <param name="overrides">
    /// Id to severity. A null value means suppressed entirely.
    /// </param>
    /// <param name="categoryOverrides">
    /// Category to severity, applied to every descriptor in the category that no
    /// id override already covers.
    /// </param>
    public GBDiagnosticOptions(
        IReadOnlyDictionary<string, GBSeverity?> overrides,
        IReadOnlyDictionary<GBDiagnosticCategory, GBSeverity?> categoryOverrides)
    {
        _overrides = overrides;
        _categoryOverrides = categoryOverrides;
    }

    /// <summary>Ids that were configured, whether or not the request was honoured.</summary>
    public IEnumerable<string> ConfiguredIds => _overrides.Keys;

    /// <summary>Categories that were configured.</summary>
    public IEnumerable<GBDiagnosticCategory> ConfiguredCategories => _categoryOverrides.Keys;

    /// <summary>
    /// The severity to report a descriptor at, or null to report nothing.
    /// </summary>
    public GBSeverity? SeverityFor(GBDiagnosticDescriptor descriptor)
    {
        if (!descriptor.IsSuppressible)
        {
            return descriptor.DefaultSeverity;
        }

        if (_overrides.TryGetValue(descriptor.Id, out GBSeverity? configured))
        {
            return configured;
        }

        if (_categoryOverrides.TryGetValue(descriptor.Category, out GBSeverity? byCategory))
        {
            return byCategory;
        }

        return descriptor.DefaultSeverity;
    }

    /// <summary>
    /// True if this was asked to change a descriptor that does not allow it.
    /// </summary>
    /// <remarks>
    /// By id only. See the class remarks for why a category is not a refusal.
    /// </remarks>
    public bool IsRefused(GBDiagnosticDescriptor descriptor) =>
        !descriptor.IsSuppressible && _overrides.ContainsKey(descriptor.Id);

    /// <summary>
    /// Parses a category as written in configuration, with or without the
    /// <see cref="CategoryPrefix"/>.
    /// </summary>
    /// <remarks>
    /// The prefixed spelling is the one <c>.editorconfig</c> uses, because that is
    /// the category Roslyn sees. The bare spelling is accepted too so a project
    /// file does not have to repeat a prefix that only means "this is a GB#
    /// setting" in a file that is entirely GB# settings.
    /// </remarks>
    public static bool TryParseCategory(string? value, out GBDiagnosticCategory category)
    {
        category = default;

        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();

        if (trimmed.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(CategoryPrefix.Length);
        }

        // Reject the numeric spellings Enum.TryParse would otherwise accept:
        // "diagnostics": { "3": "none" } is a typo, not a request to mute banking.
        if (trimmed.Length == 0 || char.IsDigit(trimmed[0]))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out category)
            && Enum.IsDefined(typeof(GBDiagnosticCategory), category);
    }

    /// <summary>
    /// Parses the spellings a developer writes in configuration.
    /// </summary>
    /// <remarks>
    /// Accepts Roslyn's vocabulary as well as GB#'s own, because a developer
    /// configuring GBS ids alongside CS ones should not have to remember which
    /// scale applies where. "none" and "silent" suppress.
    /// </remarks>
    public static bool TryParseSeverity(string? value, out GBSeverity? severity)
    {
        severity = null;

        if (value is null)
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
            case "silent":
                severity = null;
                return true;

            case "error":
                severity = GBSeverity.Error;
                return true;

            case "warning":
                severity = GBSeverity.Warning;
                return true;

            case "performance":
                severity = GBSeverity.Performance;
                return true;

            case "resource":
                severity = GBSeverity.Resource;
                return true;

            case "info":
            case "suggestion":
            case "hint":
                severity = GBSeverity.Info;
                return true;

            default:
                return false;
        }
    }
}
