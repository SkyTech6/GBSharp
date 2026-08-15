using System.Reflection;
using System.Text;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Rules;

namespace GBSharp.DocsGen;

/// <summary>
/// Emits the diagnostics reference from the descriptors in
/// <see cref="GBDiagnostics"/>. Reflection over the compiled type rather than
/// parsing the source: the compiler guarantees these are the diagnostics that
/// actually exist.
/// </summary>
public static class DiagnosticsPages
{
    private sealed record CategoryPage(GBDiagnosticCategory Category, string FileName, string Title, string Band, string Blurb);

    private static readonly CategoryPage[] Pages =
    [
        new(GBDiagnosticCategory.Language, "language.md", "Language subset", "GBS0001–GBS0099",
            "Constructs outside the GB# language subset. These are errors by design: lowering answers \"I cannot represent this\" by stopping the build."),
        new(GBDiagnosticCategory.Performance, "performance.md", "Performance", "GBS0100–GBS0199",
            "Operations that are legal but expensive on the SM83. Suppressible: they describe the hardware, not a defect in the code."),
        new(GBDiagnosticCategory.Memory, "memory.md", "Memory", "GBS0200–GBS0299",
            "WRAM, VRAM and ROM consumption - what your declarations reserve, and budgets that fail the build when exceeded."),
        new(GBDiagnosticCategory.Banking, "banking.md", "Banking", "GBS0300–GBS0399",
            "ROM banking: placement, cross-bank access, trampoline costs, and bank overflow."),
        new(GBDiagnosticCategory.CycleCost, "cycle-cost.md", "Cycle cost", "GBS0400–GBS0499",
            "Estimated cycle costs measured against the 70,224-cycle frame budget. Estimates from the IR - read them as ceilings for comparing changes, not as measurements."),
        new(GBDiagnosticCategory.Toolchain, "toolchain.md", "Toolchain", "GBS0500–GBS0599",
            "The toolchain and the build itself: missing tools, project file problems, and settings that were asked for but could not be honoured."),
        new(GBDiagnosticCategory.Assets, "assets.md", "Assets", "GBS0600–GBS0699",
            "The asset pipeline. Anything wrong with an image is reported against your C# declaration, with the fix in the message."),
        new(GBDiagnosticCategory.Internal, "internal.md", "Internal", "GBS9000+",
            "Bugs in GB# itself. Seeing one of these is worth an issue report."),
    ];

    public static int Generate(string outputDir)
    {
        List<GBDiagnosticDescriptor> all = typeof(GBDiagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(GBDiagnosticDescriptor))
            .Select(f => (GBDiagnosticDescriptor)f.GetValue(null)!)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        if (all.Count == 0)
        {
            Console.Error.WriteLine("No diagnostic descriptors found on GBDiagnostics.");
            return 1;
        }

        HashSet<string> ideReportable = GBRuleCatalog.IdeReportable.Select(d => d.Id).ToHashSet();

        Directory.CreateDirectory(outputDir);
        foreach (string stale in Directory.EnumerateFiles(outputDir, "*.md"))
        {
            File.Delete(stale);
        }

        var toc = new StringBuilder();
        toc.AppendLine("- name: Overview");
        toc.AppendLine("  href: index.md");

        var index = new StringBuilder();
        index.AppendLine("# Diagnostics reference");
        index.AppendLine();
        index.AppendLine($"Every diagnostic GB# can report - all {all.Count} of them, generated from the compiler's own descriptors in `GBSharp.Rules`, so this list cannot drift from what a build reports.");
        index.AppendLine();
        index.AppendLine("Ids are permanent once released, and banded by category. An id means the same thing in the compiler and in the editor analyzers, because both read the same definition.");
        index.AppendLine();
        index.AppendLine("## Severities");
        index.AppendLine();
        index.AppendLine("| Severity | Meaning |");
        index.AppendLine("|---|---|");
        index.AppendLine("| **Error** | Compilation cannot continue. |");
        index.AppendLine("| **Warning** | The code is suspect. |");
        index.AppendLine("| **Performance** | The code is correct but costs more than it looks like it does. |");
        index.AppendLine("| **Resource** | The code consumes a constrained resource: WRAM, VRAM, ROM, sprites. |");
        index.AppendLine("| **Info** | Informational only. |");
        index.AppendLine();
        index.AppendLine("`Performance` and `Resource` are distinct from `Warning` because they say something about the hardware rather than about the code's correctness.");
        index.AppendLine();
        index.AppendLine("## Suppressibility");
        index.AppendLine();
        index.AppendLine("A diagnostic marked *suppressible* can be silenced or downgraded per id or per category, in `gbsharp.json` or `.editorconfig` - see [Configuring diagnostics](../../guides/diagnostics-configuration.md). Errors are not suppressible: the compiler depends on them stopping the build, and downgrading one would produce nonsense C rather than a working ROM. A setting that names one anyway is answered with `GBS0508` rather than ignored.");
        index.AppendLine();
        index.AppendLine("Diagnostics marked **IDE** are additionally reported live in the editor by the Roslyn analyzers, before any build.");
        index.AppendLine();

        foreach (CategoryPage page in Pages)
        {
            List<GBDiagnosticDescriptor> members = all.Where(d => d.Category == page.Category).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            toc.AppendLine($"- name: {page.Title} ({page.Band})");
            toc.AppendLine($"  href: {page.FileName}");

            index.AppendLine($"## {page.Title} ({page.Band})");
            index.AppendLine();
            index.AppendLine("| Id | Title | Severity | |");
            index.AppendLine("|---|---|---|---|");
            foreach (GBDiagnosticDescriptor d in members)
            {
                string badges = Badges(d, ideReportable);
                index.AppendLine($"| [{d.Id}]({page.FileName}#{Anchor(d)}) | {Escape(d.Title)} | {d.DefaultSeverity} | {badges} |");
            }

            index.AppendLine();

            File.WriteAllText(Path.Combine(outputDir, page.FileName), CategoryMarkdown(page, members, ideReportable));
        }

        File.WriteAllText(Path.Combine(outputDir, "index.md"), index.ToString());
        File.WriteAllText(Path.Combine(outputDir, "toc.yml"), toc.ToString());

        Console.WriteLine($"Wrote {all.Count} diagnostics across {Pages.Count(p => all.Any(d => d.Category == p.Category))} categories to {outputDir}.");
        return 0;
    }

    private static string CategoryMarkdown(CategoryPage page, List<GBDiagnosticDescriptor> members, HashSet<string> ideReportable)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {page.Title} diagnostics ({page.Band})");
        sb.AppendLine();
        sb.AppendLine(page.Blurb);
        sb.AppendLine();

        foreach (GBDiagnosticDescriptor d in members)
        {
            sb.AppendLine($"## {d.Id}: {Escape(d.Title)}");
            sb.AppendLine();

            var facts = new List<string> { $"**Severity:** {d.DefaultSeverity}" };
            facts.Add(d.IsSuppressible ? "**Suppressible:** yes" : "**Suppressible:** no");
            if (ideReportable.Contains(d.Id))
            {
                facts.Add("**Shown live in the IDE**");
            }

            sb.AppendLine(string.Join(" · ", facts));
            sb.AppendLine();
            sb.AppendLine($"> {Escape(d.MessageFormat)}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(d.Help))
            {
                sb.AppendLine(Escape(d.Help!));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Badges(GBDiagnosticDescriptor d, HashSet<string> ideReportable)
    {
        var parts = new List<string>();
        if (d.IsSuppressible)
        {
            parts.Add("suppressible");
        }

        if (ideReportable.Contains(d.Id))
        {
            parts.Add("IDE");
        }

        return string.Join(", ", parts);
    }

    // Mirrors docfx/GitHub anchor rules for "## GBS0042: Title" closely enough
    // for the links this generator itself writes.
    private static string Anchor(GBDiagnosticDescriptor d)
    {
        string heading = $"{d.Id}: {d.Title}";
        var sb = new StringBuilder(heading.Length);
        foreach (char c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is ' ' or '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "\\|");
}
