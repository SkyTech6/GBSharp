namespace GBSharp.Compiler.Frontend;

/// <summary>
/// Everything the frontend needs to build a Roslyn compilation.
/// </summary>
/// <param name="ModuleName">Used for the ROM name and for IR module identity.</param>
/// <param name="SourceFiles">Absolute paths to the game's .cs files.</param>
/// <param name="FrameworkAssemblyPath">
/// Path to GBSharp.Framework.dll. Passed in rather than referenced directly so
/// the compiler stays independent of the framework it compiles against.
/// </param>
public sealed record CompilationRequest(
    string ModuleName,
    IReadOnlyList<string> SourceFiles,
    string FrameworkAssemblyPath)
{
    /// <summary>
    /// Converts declared assets, for the same reason the framework arrives as a
    /// path: the compiler drives image conversion without depending on an image
    /// pipeline. Defaults to reporting GBS0610 for any asset it is asked about.
    /// </summary>
    public Assets.IAssetCompiler AssetCompiler { get; init; } = Assets.NullAssetCompiler.Instance;

    /// <summary>
    /// Directories searched for an asset path, after the declaring file's own
    /// directory. Conventionally the project's Assets folder and its root.
    /// </summary>
    public IReadOnlyList<string> AssetSearchPaths { get; init; } = [];

    /// <summary>The machine assets are converted for.</summary>
    public Assets.AssetTargetProfile AssetProfile { get; init; } = Assets.AssetTargetProfile.GameBoy;

    /// <summary>
    /// Per-id severity overrides. Defaults to every descriptor's declared severity.
    /// </summary>
    public Diagnostics.GBDiagnosticOptions DiagnosticOptions { get; init; } =
        Diagnostics.GBDiagnosticOptions.Default;
}
