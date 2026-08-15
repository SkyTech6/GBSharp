using GBSharp.Compiler;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;

namespace GBSharp.Tests.Diagnostics;

/// <summary>
/// Silencing a diagnostic, and refusing to silence the ones that matter.
/// </summary>
/// <remarks>
/// GBS0201 fires on every static field a program declares. Without a way to turn
/// that off, the honest response from a developer who has accepted their WRAM
/// budget is to stop reading diagnostics altogether, which costs them the next
/// real one. The guardrail is the other half: a diagnostic the compiler depends
/// on stopping the build cannot be turned off, and asking is answered rather
/// than ignored.
/// </remarks>
public sealed class DiagnosticConfigurationTests
{
    private const string DeclaresAField = """
        using GB;

        public static class Program
        {
            private static byte frame;

            public static void Main()
            {
                Display.Enable();
                frame++;
            }
        }
        """;

    private static CompilationResult CompileWith(string source, GBDiagnosticOptions options)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "Program.cs");
        File.WriteAllText(path, source);

        var request = new CompilationRequest(
            "TestGame",
            [path],
            Path.Combine(AppContext.BaseDirectory, "GBSharp.Framework.dll"))
        {
            DiagnosticOptions = options,
        };

        return new GBSharpCompiler().Compile(request);
    }

    private static GBDiagnosticOptions Options(params (string Id, GBSeverity? Severity)[] entries) =>
        new(entries.ToDictionary(e => e.Id, e => e.Severity, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void AResourceNoteIsReportedByDefault()
    {
        CompilationResult result = CompileWith(DeclaresAField, GBDiagnosticOptions.Default);

        TestHarness.AssertReported(result.Diagnostics, "GBS0201");
    }

    [Fact]
    public void AResourceNoteCanBeSilenced()
    {
        CompilationResult result = CompileWith(DeclaresAField, Options(("GBS0201", null)));

        TestHarness.AssertNotReported(result.Diagnostics, "GBS0201");
        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
    }

    [Fact]
    public void AResourceNoteCanBeRaisedToAnError()
    {
        CompilationResult result = CompileWith(DeclaresAField, Options(("GBS0201", GBSeverity.Error)));

        GBDiagnostic reported = TestHarness.AssertReported(result.Diagnostics, "GBS0201");

        Assert.Equal(GBSeverity.Error, reported.Severity);
        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// The guardrail. Downgrading a rejection would not produce a program with a
    /// List in it; it would produce nonsense C, because lowering answers "cannot
    /// represent this" with null and relies on the build stopping.
    /// </summary>
    [Fact]
    public void ARejectionCannotBeSilenced()
    {
        CompilationResult result = CompileWith("""
            using GB;
            using System.Collections.Generic;

            public static class Program
            {
                public static List<byte> Items = new List<byte>();

                public static void Main() => Display.Enable();
            }
            """,
            Options(("GBS0042", null)));

        // Still reported, still fatal.
        TestHarness.AssertReported(result.Diagnostics, "GBS0042");
        Assert.False(result.Succeeded);

        // And the attempt is answered rather than ignored.
        TestHarness.AssertReported(result.Diagnostics, "GBS0508");
    }

    [Fact]
    public void NoRefusalIsReportedWhenNothingWasRefused()
    {
        CompilationResult result = CompileWith(DeclaresAField, Options(("GBS0201", null)));

        TestHarness.AssertNotReported(result.Diagnostics, "GBS0508");
    }

    [Theory]
    [InlineData("none", null)]
    [InlineData("silent", null)]
    [InlineData("error", GBSeverity.Error)]
    [InlineData("warning", GBSeverity.Warning)]
    [InlineData("performance", GBSeverity.Performance)]
    [InlineData("resource", GBSeverity.Resource)]
    [InlineData("info", GBSeverity.Info)]
    [InlineData("suggestion", GBSeverity.Info)]
    [InlineData("SILENT", null)]
    public void SeveritySpellingsAreAccepted(string value, GBSeverity? expected)
    {
        Assert.True(GBDiagnosticOptions.TryParseSeverity(value, out GBSeverity? severity));
        Assert.Equal(expected, severity);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownSeveritiesAreRejected(string? value) =>
        Assert.False(GBDiagnosticOptions.TryParseSeverity(value, out _));

    /// <summary>
    /// Read through Roslyn's own .editorconfig parser, so nesting, globs and
    /// precedence behave the way they do everywhere else.
    /// </summary>
    [Fact]
    public void EditorConfigSetsSeverity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_diagnostic.GBS0201.severity = none
            """);

        string source = Path.Combine(directory, "Program.cs");
        File.WriteAllText(source, DeclaresAField);

        GBDiagnosticOptions options = DiagnosticConfiguration.Read(
            projectSettings: null,
            DiagnosticConfiguration.FindConfigFiles(directory),
            source);

        Assert.Null(options.SeverityFor(GBDiagnostics.StaticAllocation));
    }

    /// <summary>
    /// The project file is the more specific statement: an .editorconfig may have
    /// been inherited from a directory the developer does not own.
    /// </summary>
    [Fact]
    public void TheProjectFileWinsOverEditorConfig()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_diagnostic.GBS0201.severity = none
            """);

        string source = Path.Combine(directory, "Program.cs");
        File.WriteAllText(source, DeclaresAField);

        GBDiagnosticOptions options = DiagnosticConfiguration.Read(
            new Dictionary<string, string> { ["GBS0201"] = "error" },
            DiagnosticConfiguration.FindConfigFiles(directory),
            source);

        Assert.Equal(GBSeverity.Error, options.SeverityFor(GBDiagnostics.StaticAllocation));
    }

    [Fact]
    public void NoConfigurationLeavesEverySeverityAlone()
    {
        foreach (GBDiagnosticDescriptor descriptor in GBDiagnostics.All)
        {
            Assert.Equal(descriptor.DefaultSeverity, GBDiagnosticOptions.Default.SeverityFor(descriptor));
        }
    }

    [Theory]
    [InlineData("GBSharp.Memory", GBDiagnosticCategory.Memory)]
    [InlineData("Memory", GBDiagnosticCategory.Memory)]
    [InlineData("gbsharp.cyclecost", GBDiagnosticCategory.CycleCost)]
    [InlineData("  Banking  ", GBDiagnosticCategory.Banking)]
    public void CategorySpellingsAreAccepted(string value, GBDiagnosticCategory expected)
    {
        Assert.True(GBDiagnosticOptions.TryParseCategory(value, out GBDiagnosticCategory category));
        Assert.Equal(expected, category);
    }

    /// <summary>
    /// An id must not parse as a category, or the project file could not tell the
    /// two scales apart. Nor should a number: <c>Enum.TryParse</c> accepts "3" for
    /// the third member, which would turn a typo into a silently applied setting.
    /// </summary>
    [Theory]
    [InlineData("GBS0201")]
    [InlineData("3")]
    [InlineData("GBSharp.")]
    [InlineData("Nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void NonCategoriesAreRejected(string? value) =>
        Assert.False(GBDiagnosticOptions.TryParseCategory(value, out _));

    /// <summary>
    /// A band arrives whole. Configuring it one id at a time means editing the
    /// setting again every time GB# learns to report something new.
    /// </summary>
    [Fact]
    public void ACategorySilencesEveryDescriptorInIt()
    {
        var options = new GBDiagnosticOptions(
            new Dictionary<string, GBSeverity?>(),
            new Dictionary<GBDiagnosticCategory, GBSeverity?> { [GBDiagnosticCategory.Memory] = null });

        foreach (GBDiagnosticDescriptor descriptor in GBDiagnostics.All
                     .Where(d => d.Category == GBDiagnosticCategory.Memory && d.IsSuppressible))
        {
            Assert.Null(options.SeverityFor(descriptor));
        }

        // And leaves the other bands alone.
        Assert.Equal(
            GBDiagnostics.BankedCall.DefaultSeverity,
            options.SeverityFor(GBDiagnostics.BankedCall));
    }

    [Fact]
    public void AnIdBeatsItsCategory()
    {
        var options = new GBDiagnosticOptions(
            new Dictionary<string, GBSeverity?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GBS0201"] = GBSeverity.Error,
            },
            new Dictionary<GBDiagnosticCategory, GBSeverity?> { [GBDiagnosticCategory.Memory] = null });

        Assert.Equal(GBSeverity.Error, options.SeverityFor(GBDiagnostics.StaticAllocation));
    }

    /// <summary>
    /// A category is a blanket statement about a band, not a claim about any
    /// particular member of it, so it does not refuse the way naming a
    /// non-suppressible id does.
    /// </summary>
    [Fact]
    public void ACategoryNeitherSilencesNorRefusesANonSuppressibleDescriptor()
    {
        var options = new GBDiagnosticOptions(
            new Dictionary<string, GBSeverity?>(),
            new Dictionary<GBDiagnosticCategory, GBSeverity?> { [GBDiagnosticCategory.Language] = null });

        Assert.Equal(
            GBDiagnostics.DynamicCollection.DefaultSeverity,
            options.SeverityFor(GBDiagnostics.DynamicCollection));

        Assert.False(options.IsRefused(GBDiagnostics.DynamicCollection));
    }

    /// <summary>
    /// This is the half that did not work before. Roslyn applies the category form
    /// to an analyzer's own descriptors in the IDE, but leaves it among the
    /// free-form options, so a command-line build saw nothing.
    /// </summary>
    [Fact]
    public void EditorConfigSetsSeverityByCategory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_analyzer_diagnostic.category-GBSharp.Memory.severity = none
            """);

        string source = Path.Combine(directory, "Program.cs");
        File.WriteAllText(source, DeclaresAField);

        GBDiagnosticOptions options = DiagnosticConfiguration.Read(
            projectSettings: null,
            DiagnosticConfiguration.FindConfigFiles(directory),
            source);

        Assert.Null(options.SeverityFor(GBDiagnostics.StaticAllocation));
        Assert.Equal(
            GBDiagnostics.BankedCall.DefaultSeverity,
            options.SeverityFor(GBDiagnostics.BankedCall));
    }

    [Fact]
    public void AnotherAnalyzersCategoryIsLeftAlone()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_analyzer_diagnostic.category-Style.severity = none
            """);

        string source = Path.Combine(directory, "Program.cs");
        File.WriteAllText(source, DeclaresAField);

        GBDiagnosticOptions options = DiagnosticConfiguration.Read(
            projectSettings: null,
            DiagnosticConfiguration.FindConfigFiles(directory),
            source);

        Assert.Empty(options.ConfiguredCategories);
    }

    [Fact]
    public void TheProjectFileTakesACategoryToo()
    {
        CompilationResult result = CompileWith(
            DeclaresAField,
            DiagnosticConfiguration.Read(
                new Dictionary<string, string> { ["GBSharp.Memory"] = "none" },
                [],
                sourcePath: null));

        TestHarness.AssertNotReported(result.Diagnostics, "GBS0201");
        Assert.True(result.Succeeded, TestHarness.Describe(result.Diagnostics));
    }
}
