using System.Text;
using System.Xml.Linq;

namespace GBSharp.DocsGen;

/// <summary>
/// Flattens the compiler-generated XML documentation file for
/// GBSharp.Framework into one markdown document for llms-full.txt.
/// </summary>
public static class ApiMarkdown
{
    public static string FromXmlDocFile(string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        List<XElement> members = doc.Root?.Element("members")?.Elements("member").ToList() ?? [];

        // Group members under their declaring type, in the order types appear.
        var types = new List<(string Name, XElement? Doc, List<(string Signature, XElement Doc)> Members)>();
        var byType = new Dictionary<string, int>();

        foreach (XElement member in members)
        {
            string name = member.Attribute("name")?.Value ?? "";
            if (name.Length < 3)
            {
                continue;
            }

            char kind = name[0];
            string rest = name[2..];

            if (kind == 'T')
            {
                if (!byType.ContainsKey(rest))
                {
                    byType[rest] = types.Count;
                    types.Add((rest, member, []));
                }
                else
                {
                    types[byType[rest]] = (rest, member, types[byType[rest]].Members);
                }

                continue;
            }

            // M:GB.Sprites.Move(System.Byte,...), P:GB.Hardware.Sprites, F:GB.Button.A, E:...
            string withoutArgs = rest;
            int paren = withoutArgs.IndexOf('(');
            if (paren >= 0)
            {
                withoutArgs = withoutArgs[..paren];
            }

            int lastDot = withoutArgs.LastIndexOf('.');
            if (lastDot < 0)
            {
                continue;
            }

            string typeName = withoutArgs[..lastDot];
            if (!byType.TryGetValue(typeName, out int index))
            {
                byType[typeName] = types.Count;
                types.Add((typeName, null, []));
                index = byType[typeName];
            }

            types[index].Members.Add((rest, member));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Framework API reference (GB namespace)");
        sb.AppendLine();
        sb.AppendLine("The reference assembly game code compiles against. Members are declarations only; the GB# compiler maps each [Native] member to a C symbol during lowering. The namespace is `GB`.");
        sb.AppendLine();

        foreach ((string name, XElement? typeDoc, List<(string Signature, XElement Doc)> typeMembers) in types)
        {
            sb.AppendLine($"## {Friendly(name)}");
            sb.AppendLine();
            if (typeDoc is not null)
            {
                AppendDoc(sb, typeDoc);
            }

            foreach ((string signature, XElement memberDoc) in typeMembers)
            {
                sb.AppendLine($"### {Friendly(signature)}");
                sb.AppendLine();
                AppendDoc(sb, memberDoc);
            }
        }

        return sb.ToString();
    }

    private static void AppendDoc(StringBuilder sb, XElement member)
    {
        string summary = Text(member.Element("summary"));
        if (summary.Length > 0)
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        foreach (XElement param in member.Elements("param"))
        {
            string name = param.Attribute("name")?.Value ?? "";
            string text = Text(param);
            if (text.Length > 0)
            {
                sb.AppendLine($"- `{name}`: {text}");
            }
        }

        if (member.Elements("param").Any())
        {
            sb.AppendLine();
        }

        string returns = Text(member.Element("returns"));
        if (returns.Length > 0)
        {
            sb.AppendLine($"Returns: {returns}");
            sb.AppendLine();
        }

        string remarks = Text(member.Element("remarks"));
        if (remarks.Length > 0)
        {
            sb.AppendLine(remarks);
            sb.AppendLine();
        }
    }

    /// <summary>Renders an XML doc element to plain markdown-ish text.</summary>
    private static string Text(XElement? element)
    {
        if (element is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        Render(element, sb);

        // Collapse the whitespace the XML writer wrapped lines with.
        string[] lines = sb.ToString().Split('\n');
        var result = new StringBuilder();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (result.Length > 0 && !result.ToString().EndsWith("\n\n"))
                {
                    result.Append("\n\n");
                }
            }
            else
            {
                if (result.Length > 0 && !result.ToString().EndsWith('\n'))
                {
                    result.Append(' ');
                }

                result.Append(trimmed);
            }
        }

        return result.ToString().Trim();
    }

    private static void Render(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText text:
                sb.Append(text.Value);
                break;

            case XElement el:
                switch (el.Name.LocalName)
                {
                    case "c":
                    case "code":
                        sb.Append('`').Append(el.Value.Trim()).Append('`');
                        break;

                    case "see":
                    case "seealso":
                        string cref = el.Attribute("cref")?.Value ?? el.Attribute("href")?.Value ?? "";
                        sb.Append('`').Append(Friendly(cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref)).Append('`');
                        break;

                    case "paramref":
                    case "typeparamref":
                        sb.Append('`').Append(el.Attribute("name")?.Value).Append('`');
                        break;

                    case "para":
                        foreach (XNode child in el.Nodes())
                        {
                            Render(child, sb);
                        }

                        sb.Append("\n\n");
                        break;

                    default:
                        foreach (XNode child in el.Nodes())
                        {
                            Render(child, sb);
                        }

                        break;
                }

                break;
        }
    }

    /// <summary>Shortens doc-comment ids for reading: strips the GB. prefix and System. parameter prefixes.</summary>
    private static string Friendly(string id)
    {
        string result = id
            .Replace("System.", "")
            .Replace("GB.Collections.", "")
            .Replace("GB.", "");

        // M:ns.Type.#ctor → Type constructor
        result = result.Replace(".#ctor", " constructor");

        // Generic arity markers read badly: FixedList`1 → FixedList<T>
        result = result.Replace("`2", "<T1,T2>").Replace("`1", "<T>").Replace("{", "<").Replace("}", ">");

        return result;
    }
}
