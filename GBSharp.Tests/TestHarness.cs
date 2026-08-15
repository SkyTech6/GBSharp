using GBSharp.Assets.Pipeline;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Backend.GBDK.Toolchain;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis.Diagnostics;

namespace GBSharp.Tests;

/// <summary>
/// Compiles C# source strings through the real pipeline.
/// </summary>
/// <remarks>
/// Everything up to and including C emission runs without GBDK, so most of the
/// suite is testable on a bare checkout. Only <see cref="BuildRom"/> needs the
/// toolchain, and those tests skip themselves when it is absent.
/// </remarks>
public static class TestHarness
{
    /// <summary>Wraps a method body in the smallest program GB# will accept.</summary>
    public static string Program(string body, string extra = "") => $$"""
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            public static void Main()
            {
        {{body}}
            }
        }

        {{extra}}
        """;

    public static CompilationResult Compile(string source, string moduleName = "TestGame") =>
        CompileWithAssets(source, new Dictionary<string, byte[]>(), AssetTargetProfile.GameBoy, moduleName);

    /// <summary>
    /// Compiles source alongside images written next to it.
    /// </summary>
    /// <param name="images">File name to PNG bytes, written into the project directory.</param>
    public static CompilationResult CompileWithAssets(
        string source,
        IReadOnlyDictionary<string, byte[]> images,
        AssetTargetProfile profile = AssetTargetProfile.GameBoy,
        string moduleName = "TestGame")
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "Program.cs");
        File.WriteAllText(path, source);

        foreach ((string name, byte[] bytes) in images)
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }

        var request = new CompilationRequest(moduleName, [path], FrameworkAssemblyPath)
        {
            AssetCompiler = new PngAssetCompiler(),
            AssetSearchPaths = [directory],
            AssetProfile = profile,
        };

        return new GBSharpCompiler().Compile(request);
    }

    /// <summary>Compiles and asserts success, returning the module.</summary>
    public static IRModule CompileModule(string source, string moduleName = "TestGame")
    {
        CompilationResult result = Compile(source, moduleName);

        Assert.True(
            result.Succeeded,
            "Expected compilation to succeed but got:\n" + Describe(result.Diagnostics));

        return result.Module!;
    }

    /// <summary>
    /// Every generated file concatenated, for assertions about the program as a
    /// whole rather than about which file a declaration landed in.
    /// </summary>
    public static string EmitC(string source, string moduleName = "TestGame", bool annotateSource = false) =>
        string.Join("\n", EmitFiles(source, moduleName, annotateSource).Select(f => f.Text));

    /// <summary>The generated files individually, for assertions about the split.</summary>
    public static IReadOnlyList<EmittedFile> EmitFiles(
        string source,
        string moduleName = "TestGame",
        bool annotateSource = false) =>
        new CEmitter(annotateSource).Emit(CompileModule(source, moduleName));

    /// <summary>
    /// Emits with <c>--annotate-source</c> on, returning both the C and the
    /// source map <see cref="CEmitter"/> built alongside it.
    /// </summary>
    public static (IReadOnlyList<EmittedFile> Files, IReadOnlyList<SourceMapEntry> SourceMap) EmitAnnotated(
        string source,
        string moduleName = "TestGame")
    {
        var emitter = new CEmitter(annotateSource: true);
        IReadOnlyList<EmittedFile> files = emitter.Emit(CompileModule(source, moduleName));
        return (files, emitter.SourceMap);
    }

    /// <summary>Compiles all the way to a ROM. Requires GBDK.</summary>
    public static RomBuildResult BuildRom(
        string source,
        string moduleName = "TestGame",
        GBTarget target = GBTarget.GameBoy,
        bool annotateSource = false)
    {
        IRModule module = CompileModule(source, moduleName);
        return Link(module, target, annotateSource);
    }

    /// <summary>Compiles source and its images all the way to a ROM. Requires GBDK.</summary>
    public static RomBuildResult BuildRomWithAssets(
        string source,
        IReadOnlyDictionary<string, byte[]> images,
        GBTarget target = GBTarget.GameBoy)
    {
        AssetTargetProfile profile = target == GBTarget.GameBoyColor
            ? AssetTargetProfile.GameBoyColor
            : AssetTargetProfile.GameBoy;

        CompilationResult result = CompileWithAssets(source, images, profile);

        Assert.True(result.Succeeded, "Expected compilation to succeed but got:\n" + Describe(result.Diagnostics));

        return Link(result.Module!, target);
    }

    private static RomBuildResult Link(IRModule module, GBTarget target, bool annotateSource = false)
    {
        string outputDirectory = Path.Combine(CreateTempDirectory(), "build");
        var options = new RomBuildOptions(outputDirectory, target, KeepGeneratedC: true)
        {
            AnnotateSource = annotateSource,
        };
        return new RomBuilder().Build(module, options);
    }

    /// <summary>
    /// Set this environment variable to insist the toolchain is present.
    /// </summary>
    public const string RequireGbdkVariable = "GBSHARP_REQUIRE_GBDK";

    /// <summary>True when a GBDK install is available for integration tests.</summary>
    /// <remarks>
    /// xUnit v2 has no runtime skip, so the integration tests return early when
    /// this is false. That makes a bare checkout run green, and it also means a
    /// broken toolchain lookup would turn the whole integration layer into a
    /// silent pass. Setting <see cref="RequireGbdkVariable"/> converts a missing
    /// toolchain from "skip quietly" into a loud failure, which is what CI does
    /// once it has fetched GBDK: after that point, skipping can only be a bug.
    /// </remarks>
    public static bool GbdkAvailable
    {
        get
        {
            bool located = GbdkToolchain.TryLocate(null, out GbdkToolchain? toolchain, out IReadOnlyList<string> searched)
                && toolchain is not null;

            if (!located && RequireGbdk)
            {
                throw new InvalidOperationException(
                    $"{RequireGbdkVariable} is set, but no GBDK install was found. " +
                    "The integration tests would otherwise have skipped themselves silently. Looked in: " +
                    string.Join(", ", searched.Take(4)));
            }

            return located;
        }
    }

    private static bool RequireGbdk =>
        IsRequireValue(Environment.GetEnvironmentVariable(RequireGbdkVariable));

    /// <summary>
    /// Whether a value of <see cref="RequireGbdkVariable"/>, or of
    /// <see cref="GameBoyTest.RequireEmulatorVariable"/>, means "insist".
    /// </summary>
    /// <remarks>
    /// Unset, empty, "0" and "false" all mean no, so a shell that exports the
    /// variable as empty does not accidentally arm the check. Anything else
    /// means yes: the point of the variable is to be hard to disarm by accident.
    /// </remarks>
    public static bool IsRequireValue(string? value) =>
        value is { Length: > 0 } &&
        !string.Equals(value, "0", StringComparison.Ordinal) &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<GBDiagnostic> DiagnosticsFor(string source) => Compile(source).Diagnostics;

    /// <summary>
    /// Runs the analyzers over source the way an IDE would, without a build.
    /// </summary>
    /// <remarks>
    /// Uses the same Roslyn compilation the compiler builds, so the two paths
    /// see identical symbols and any difference in what they report is a real
    /// difference in the rules rather than in the setup.
    /// </remarks>
    public static IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> Analyze(string source)
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "Program.cs");
        File.WriteAllText(path, source);

        var request = new CompilationRequest("TestGame", [path], FrameworkAssemblyPath);
        var diagnostics = new DiagnosticBag();

        Microsoft.CodeAnalysis.CSharp.CSharpCompilation? compilation =
            RoslynFrontend.Create(request, diagnostics);

        Assert.True(compilation is not null, "the compilation could not be created:\n" + Describe(diagnostics.Diagnostics));

        return compilation!
            .WithAnalyzers([new GBSharp.Analyzers.GBSubsetAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Asserts a diagnostic id was reported, with a readable failure otherwise.</summary>
    public static GBDiagnostic AssertReported(IReadOnlyList<GBDiagnostic> diagnostics, string id)
    {
        GBDiagnostic? match = diagnostics.FirstOrDefault(d => d.Id == id);

        Assert.True(match is not null, $"Expected {id} but got:\n{Describe(diagnostics)}");
        return match!;
    }

    public static void AssertNotReported(IReadOnlyList<GBDiagnostic> diagnostics, string id) =>
        Assert.True(
            diagnostics.All(d => d.Id != id),
            $"Did not expect {id} but got:\n{Describe(diagnostics)}");

    public static string Describe(IReadOnlyList<GBDiagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "  (no diagnostics)"
            : string.Join("\n", diagnostics.Select(d => $"  {d.Severity} {d.Id} at line {d.Span.Line}: {d.Message}"));

    private static string FrameworkAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "GBSharp.Framework.dll");

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The checkout root, for tests that assert against files rather than behaviour.
    /// </summary>
    /// <remarks>
    /// Found by walking up for the solution file rather than by counting
    /// directories up from the test binary, which would break the first time the
    /// output path changed.
    /// </remarks>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GBSharp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
