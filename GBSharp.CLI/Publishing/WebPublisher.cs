using System.Text;

namespace GBSharp.Cli.Publishing;

/// <summary>
/// Publishes a game as a web page.
/// </summary>
/// <remarks>
/// <para>
/// The same ROM and the same settings as a native publish, over the same
/// emulator ABI, with a browser standing in for the window and the sound
/// device. Two layouts come out of it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A folder of files, which is what you upload to a static host.
/// </description></item>
/// <item><description>
/// One <c>.html</c> with the wasm and the ROM inlined as base64, which is what
/// you email to somebody. It opens from a file manager, with no server, because
/// there is nothing left for the page to fetch.
/// </description></item>
/// </list>
/// <para>
/// The multi-file layout needs to be served over http rather than opened from
/// disk, because a module script and a wasm fetch are both blocked under
/// <c>file://</c>. That is the browser's rule and not something publishing can
/// route around, which is exactly why the single file mode exists.
/// </para>
/// </remarks>
public static class WebPublisher
{
    /// <summary>The RID that means "a browser".</summary>
    public const string Rid = "web";

    /// <summary>
    /// Writes the published game, returning the file a person would open.
    /// </summary>
    public static string Write(
        string webRuntimeDirectory,
        string playerAssetDirectory,
        string outputDirectory,
        byte[] rom,
        string title,
        string config,
        bool singleFile)
    {
        Directory.CreateDirectory(outputDirectory);

        string template = File.ReadAllText(Path.Combine(playerAssetDirectory, "index.html"));
        string page = template
            .Replace("{{TITLE}}", HtmlEscape(title))
            .Replace("{{CONFIG}}", config);

        if (!singleFile)
        {
            foreach (string name in new[] { "player.js" })
            {
                File.Copy(
                    Path.Combine(playerAssetDirectory, name),
                    Path.Combine(outputDirectory, name),
                    overwrite: true);
            }

            foreach (string name in new[] { "gbsharp.js", "gbsharp.wasm", "gbsharp-runtime.js" })
            {
                File.Copy(
                    Path.Combine(webRuntimeDirectory, name),
                    Path.Combine(outputDirectory, name),
                    overwrite: true);
            }

            File.WriteAllBytes(Path.Combine(outputDirectory, "game.gb"), rom);

            string indexPath = Path.Combine(outputDirectory, "index.html");
            File.WriteAllText(indexPath, page);
            return indexPath;
        }

        // Everything the page would have fetched, inlined. The two module
        // imports have to be rewritten as blob URLs, because a module script
        // cannot import a path that no longer exists.
        string runtimeModule = File.ReadAllText(Path.Combine(webRuntimeDirectory, "gbsharp-runtime.js"));
        string emscriptenModule = File.ReadAllText(Path.Combine(webRuntimeDirectory, "gbsharp.js"));
        string playerModule = File.ReadAllText(Path.Combine(playerAssetDirectory, "player.js"));

        byte[] wasm = File.ReadAllBytes(Path.Combine(webRuntimeDirectory, "gbsharp.wasm"));

        var inlined = new StringBuilder();
        inlined.Append("<script type=\"application/base64\" id=\"wasm\">")
               .Append(Convert.ToBase64String(wasm))
               .AppendLine("</script>");
        inlined.Append("<script type=\"application/base64\" id=\"rom\">")
               .Append(Convert.ToBase64String(rom))
               .AppendLine("</script>");

        // Modules resolved through an import map pointing at blob URLs, which
        // is the one mechanism that lets `import './player.js'` keep working
        // with no file called player.js anywhere.
        inlined.AppendLine("<script>")
               .AppendLine("const gbsharpModules = {")
               .AppendLine($"  './gbsharp-runtime.js': {JavaScriptString(runtimeModule)},")
               .AppendLine($"  './gbsharp.js': {JavaScriptString(emscriptenModule)},")
               .AppendLine($"  './player.js': {JavaScriptString(playerModule)},")
               .AppendLine("};")
               .AppendLine("const gbsharpUrls = {};")
               .AppendLine("for (const [name, source] of Object.entries(gbsharpModules)) {")
               .AppendLine("  gbsharpUrls[name] = URL.createObjectURL(")
               .AppendLine("    new Blob([source], { type: 'text/javascript' }));")
               .AppendLine("}")
               // Rewritten in dependency order, so each module's own imports
               // point at the blob of the module it needs.
               .AppendLine("for (const name of ['./gbsharp-runtime.js', './gbsharp.js', './player.js']) {")
               .AppendLine("  let source = gbsharpModules[name];")
               .AppendLine("  for (const [target, url] of Object.entries(gbsharpUrls)) {")
               .AppendLine("    source = source.split(`'${target}'`).join(`'${url}'`);")
               .AppendLine("  }")
               .AppendLine("  gbsharpUrls[name] = URL.createObjectURL(")
               .AppendLine("    new Blob([source], { type: 'text/javascript' }));")
               .AppendLine("}")
               .AppendLine("const map = document.createElement('script');")
               .AppendLine("map.type = 'importmap';")
               .AppendLine("map.textContent = JSON.stringify({ imports: gbsharpUrls });")
               .AppendLine("document.currentScript.after(map);")
               .AppendLine("</script>");

        // Before the module script that imports any of it.
        page = page.Replace("<script type=\"module\">", inlined + "<script type=\"module\">");

        string singlePath = Path.Combine(outputDirectory, SafeFileName(title) + ".html");
        File.WriteAllText(singlePath, page);
        return singlePath;
    }

    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// A JavaScript string literal for source that itself contains quotes,
    /// backslashes, newlines and possibly a closing script tag.
    /// </summary>
    private static string JavaScriptString(string source)
    {
        var text = new StringBuilder(source.Length + 64);
        text.Append('"');

        foreach (char c in source)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                // The parser looks for this sequence inside a script element
                // regardless of what it means to JavaScript.
                case '<': text.Append("\\u003c"); break;
                default: text.Append(c); break;
            }
        }

        text.Append('"');
        return text.ToString();
    }

    private static string SafeFileName(string title)
    {
        var name = new StringBuilder(title.Length);

        foreach (char c in title)
        {
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c);
        }

        string result = name.ToString().Trim();
        return result.Length == 0 ? "game" : result;
    }
}
