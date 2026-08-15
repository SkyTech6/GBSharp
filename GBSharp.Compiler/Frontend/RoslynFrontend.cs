using GBSharp.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GBSharp.Compiler.Frontend;

/// <summary>
/// Builds the Roslyn compilation that GB# analyses.
/// </summary>
/// <remarks>
/// GB# does not parse C# (thesis section 5.1). Everything downstream works from
/// Roslyn's semantic model, so this is the only place source text is read.
/// </remarks>
public static class RoslynFrontend
{
    /// <summary>C# version the GB# subset is defined against.</summary>
    public const LanguageVersion Language = LanguageVersion.CSharp12;

    public static CSharpCompilation? Create(CompilationRequest request, DiagnosticBag diagnostics)
    {
        if (request.SourceFiles.Count == 0)
        {
            diagnostics.Report(GBDiagnostics.NoSourceFiles, SourceSpan.None, request.ModuleName);
            return null;
        }

        var parseOptions = new CSharpParseOptions(Language, DocumentationMode.None, SourceCodeKind.Regular);

        var trees = new List<SyntaxTree>(request.SourceFiles.Count);
        foreach (string path in request.SourceFiles)
        {
            string text = File.ReadAllText(path);
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path));
        }

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            // The game never runs on .NET, so nothing here affects the ROM. These
            // settings only shape the diagnostics the developer sees.
            allowUnsafe: false,
            platform: Platform.AnyCpu,
            reportSuppressedDiagnostics: false,
            specificDiagnosticOptions: SuppressedRoslynWarnings);

        return CSharpCompilation.Create(
            request.ModuleName,
            trees,
            BuildReferences(request.FrameworkAssemblyPath),
            options);
    }

    /// <summary>
    /// Roslyn warnings that are wrong about GB# rather than about the code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CS0649 says a field is never assigned and will keep its default. That is
    /// exactly right and exactly intended for an <c>[Asset]</c> field, whose
    /// contents come from an image at build time and which the developer must
    /// never assign. Leaving it on would put an unfixable warning on every
    /// asset declaration.
    /// </para>
    /// <para>
    /// Nothing else is suppressed. GB# speaks for itself only where it knows
    /// better than Roslyn does.
    /// </para>
    /// </remarks>
    private static readonly KeyValuePair<string, ReportDiagnostic>[] SuppressedRoslynWarnings =
    [
        new("CS0649", ReportDiagnostic.Suppress),
    ];

    /// <summary>
    /// References the host's framework assemblies plus GBSharp.Framework.
    /// </summary>
    /// <remarks>
    /// Referencing the full BCL looks wrong for a target that has none of it,
    /// but it is deliberate: it lets <c>List&lt;T&gt;</c> and <c>string</c>
    /// *resolve*, so GB# can answer with GBS0042 and GBS0043 and a suggested
    /// alternative. Without the references, the developer would get Roslyn's
    /// "type or namespace not found" instead, which teaches nothing.
    /// </remarks>
    private static IReadOnlyList<MetadataReference> BuildReferences(string frameworkAssemblyPath)
    {
        var references = new List<MetadataReference>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (string path in trusted.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }
        else
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        if (File.Exists(frameworkAssemblyPath))
        {
            references.Add(MetadataReference.CreateFromFile(frameworkAssemblyPath));
        }

        return references;
    }

    /// <summary>
    /// Copies Roslyn's own errors and warnings into the GB# bag.
    /// </summary>
    /// <remarks>
    /// If the source is not valid C#, Roslyn's wording is the clearest answer
    /// available and GB# has nothing to add. GB# only speaks for itself once the
    /// code compiles as C#.
    /// </remarks>
    public static void ReportRoslynDiagnostics(CSharpCompilation compilation, DiagnosticBag diagnostics)
    {
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                diagnostics.ReportRoslyn(diagnostic);
            }
        }
    }
}
