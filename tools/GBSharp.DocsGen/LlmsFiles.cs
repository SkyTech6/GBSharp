using System.Text;
using System.Xml.Linq;

namespace GBSharp.DocsGen;

/// <summary>
/// Emits llms.txt and llms-full.txt (per llmstxt.org) into the built site, and
/// copies the source markdown into <c>_site/md/</c> so the links in llms.txt
/// resolve to raw markdown rather than rendered HTML.
/// </summary>
public static class LlmsFiles
{
    private sealed record Page(string RelativePath, string Title, string Description, string Content);

    private static readonly (string Dir, string Name)[] Sections =
    [
        ("getting-started", "Getting Started"),
        ("tutorials", "Tutorials"),
        ("guides", "Guides"),
        ("reference", "Reference"),
    ];

    public static int Generate(string docsDir, string siteDir, string? frameworkXml, string baseUrl)
    {
        if (!Directory.Exists(siteDir))
        {
            Console.Error.WriteLine($"Site directory '{siteDir}' does not exist. Run docfx first.");
            return 1;
        }

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        var sections = new List<(string Name, List<Page> Pages)>();
        foreach ((string dir, string name) in Sections)
        {
            string sectionDir = Path.Combine(docsDir, dir);
            var pages = new List<Page>();
            foreach (string relative in TocOrder(sectionDir))
            {
                string path = Path.Combine(sectionDir, relative);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"toc.yml in {sectionDir} names '{relative}', which does not exist.");
                    return 1;
                }

                pages.Add(Load(Path.Combine(dir, relative).Replace('\\', '/'), path));
            }

            sections.Add((name, pages));
        }

        // The generated diagnostics pages live under reference/ but have their
        // own toc; fold them into the Reference section after the hand-written
        // pages so the index lists every id's category page.
        string diagnosticsDir = Path.Combine(docsDir, "reference", "diagnostics");
        if (Directory.Exists(diagnosticsDir))
        {
            List<Page> reference = sections.Single(s => s.Name == "Reference").Pages;
            foreach (string relative in TocOrder(diagnosticsDir))
            {
                reference.Add(Load($"reference/diagnostics/{relative}", Path.Combine(diagnosticsDir, relative)));
            }
        }

        Page root = Load("index.md", Path.Combine(docsDir, "index.md"));

        CopyMarkdown(docsDir, siteDir, sections.SelectMany(s => s.Pages).Append(root));

        File.WriteAllText(Path.Combine(siteDir, "llms.txt"), BuildIndex(baseUrl, sections));

        string apiFlattening = frameworkXml is not null && File.Exists(frameworkXml)
            ? ApiMarkdown.FromXmlDocFile(frameworkXml)
            : "";
        File.WriteAllText(Path.Combine(siteDir, "llms-full.txt"), BuildFull(root, sections, apiFlattening));

        Console.WriteLine($"Wrote llms.txt and llms-full.txt to {siteDir} ({sections.Sum(s => s.Pages.Count)} pages{(apiFlattening.Length > 0 ? " + API reference" : "")}).");
        if (apiFlattening.Length == 0)
        {
            Console.Error.WriteLine("warning: no framework XML doc file given or found; llms-full.txt has no API section.");
        }

        return 0;
    }

    private static string BuildIndex(string baseUrl, List<(string Name, List<Page> Pages)> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GB#");
        sb.AppendLine();
        sb.AppendLine("> A statically compiled, hardware-aware C# development environment for the Game Boy and Game Boy Color. You write a constrained subset of C#; GB# analyses it with Roslyn, lowers it to its own IR, emits conservative C, and hands that to GBDK-2020/SDCC to produce a .gb or .gbc ROM. There is no CLR, no JIT, and no garbage collector on the target.");
        sb.AppendLine();
        sb.AppendLine("Game code is written against the `GB` namespace (`using GB;` and `using static GB.Hardware;`). Everything is driven by the `gbsharp` CLI: new, build, run, profile, publish, clean, analyze, assets, doctor. Assets are PNGs bound with attributes like `[Asset]` and converted at build time. `static readonly` data goes to ROM; everything else is WRAM. `[Bank(n)]` places code and data in ROM banks. Diagnostics are stable `GBSxxxx` ids banded by category.");
        sb.AppendLine();

        foreach ((string name, List<Page> pages) in sections)
        {
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            foreach (Page page in pages)
            {
                sb.AppendLine($"- [{page.Title}]({baseUrl}md/{page.RelativePath}): {page.Description}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Optional");
        sb.AppendLine();
        sb.AppendLine($"- [llms-full.txt]({baseUrl}llms-full.txt): the complete manual - every page above, the full diagnostics reference, and the framework API - in one file.");
        sb.AppendLine($"- [Design thesis](https://github.com/SkyTech6/GBSharp/blob/main/GBSharp_Thesis_and_Architecture.md): why GB# is a compiled subset rather than a runtime, and the architecture behind it.");

        return sb.ToString();
    }

    private static string BuildFull(Page root, List<(string Name, List<Page> Pages)> sections, string apiFlattening)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GB# - complete documentation");
        sb.AppendLine();
        sb.AppendLine("> This file concatenates the entire GB# user manual for LLM consumption: every documentation page in navigation order, the generated diagnostics reference, and the framework API reference.");
        sb.AppendLine();
        Append(sb, root);

        foreach ((string _, List<Page> pages) in sections)
        {
            foreach (Page page in pages)
            {
                Append(sb, page);
            }
        }

        if (apiFlattening.Length > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(apiFlattening);
        }

        return sb.ToString();

        static void Append(StringBuilder sb, Page page)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"<!-- {page.RelativePath} -->");
            sb.AppendLine();
            sb.AppendLine(page.Content.Trim());
            sb.AppendLine();
        }
    }

    private static void CopyMarkdown(string docsDir, string siteDir, IEnumerable<Page> pages)
    {
        foreach (Page page in pages)
        {
            string target = Path.Combine(siteDir, "md", page.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(docsDir, page.RelativePath), target, overwrite: true);
        }
    }

    private static Page Load(string relativePath, string path)
    {
        string content = File.ReadAllText(path);
        string[] lines = content.Split('\n');

        string title = lines.FirstOrDefault(l => l.StartsWith("# "))?.Trim()[2..].Trim()
                       ?? Path.GetFileNameWithoutExtension(path);

        // First non-heading prose paragraph, first sentence, as the one-line description.
        string description = "";
        var paragraph = new StringBuilder();
        bool seenTitle = false;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("# "))
            {
                seenTitle = true;
                continue;
            }

            if (!seenTitle)
            {
                continue;
            }

            if (line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("```") || line.StartsWith('>'))
            {
                if (paragraph.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Length > 0)
                {
                    break;
                }

                continue;
            }

            paragraph.Append(paragraph.Length > 0 ? " " : "").Append(line.Trim());
        }

        if (paragraph.Length > 0)
        {
            description = paragraph.ToString();
            int cut = description.IndexOf(". ", StringComparison.Ordinal);
            if (cut > 0)
            {
                description = description[..(cut + 1)];
            }

            // Strip markdown links and emphasis for a clean one-liner.
            description = System.Text.RegularExpressions.Regex.Replace(description, @"\[([^\]]+)\]\([^)]*\)", "$1");
            description = description.Replace("**", "").Replace("*", "");
        }

        return new Page(relativePath, title, description, content);
    }

    /// <summary>
    /// Reads the page order from a section's toc.yml. Only the two-line
    /// <c>- name:</c> / <c>href:</c> form this repo writes is understood.
    /// </summary>
    private static List<string> TocOrder(string sectionDir)
    {
        var order = new List<string>();
        string toc = Path.Combine(sectionDir, "toc.yml");
        if (!File.Exists(toc))
        {
            return order;
        }

        foreach (string line in File.ReadAllLines(toc))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("href:"))
            {
                string href = trimmed["href:".Length..].Trim();
                if (href.EndsWith(".md") && !href.Contains('/'))
                {
                    order.Add(href);
                }
            }
        }

        return order;
    }
}
