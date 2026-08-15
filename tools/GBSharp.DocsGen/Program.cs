using GBSharp.DocsGen;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: GBSharp.DocsGen diagnostics <output-dir>");
    Console.Error.WriteLine("       GBSharp.DocsGen llms <docs-dir> <site-dir> [framework-xml] [base-url]");
    return 1;
}

switch (args[0])
{
    case "diagnostics":
        return DiagnosticsPages.Generate(args.Length > 1 ? args[1] : "docs/reference/diagnostics");

    case "llms":
        return LlmsFiles.Generate(
            docsDir: args.Length > 1 ? args[1] : "docs",
            siteDir: args.Length > 2 ? args[2] : "docs/_site",
            frameworkXml: args.Length > 3 ? args[3] : null,
            baseUrl: args.Length > 4 ? args[4] : "https://skytech6.github.io/GBSharp/");

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 1;
}
