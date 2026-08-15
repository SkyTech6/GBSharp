using GBSharp.Cli;
using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Tests;

/// <summary>
/// <c>gbsharp.json</c> parsing and validation, independent of a real build.
/// </summary>
public sealed class GameProjectTests
{
    private static string CreateTempProjectDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-tests", "project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void LibrariesIsAbsentByDefault()
    {
        string directory = CreateTempProjectDirectory();
        GameProject project = GameProject.Load(directory);

        Assert.Null(project.Libraries);
        Assert.Empty(project.ResolvedLibraries);
    }

    [Fact]
    public void LibrariesResolveRelativeToTheProjectDirectory()
    {
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(
            Path.Combine(directory, "hugedriver.lib"),
            "not a real library, just something that exists on disk");

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "libraries": ["hugedriver.lib"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.Equal(["hugedriver.lib"], project.Libraries!);
        Assert.Equal([Path.GetFullPath(Path.Combine(directory, "hugedriver.lib"))], project.ResolvedLibraries);
    }

    [Fact]
    public void LibrariesResolveIntoASubdirectory()
    {
        string directory = CreateTempProjectDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "lib"));
        File.WriteAllText(Path.Combine(directory, "lib", "hugedriver.lib"), "stub");

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "libraries": ["lib/hugedriver.lib"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.Equal(
            [Path.GetFullPath(Path.Combine(directory, "lib", "hugedriver.lib"))],
            project.ResolvedLibraries);
    }

    [Fact]
    public void ValidateAcceptsALibraryThatExists()
    {
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, "hugedriver.lib"), "stub");

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "libraries": ["hugedriver.lib"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
    }

    [Fact]
    public void ValidateRejectsALibraryThatDoesNotExist()
    {
        string directory = CreateTempProjectDirectory();

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "libraries": ["missing.lib"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.False(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics));
        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0511");
        Assert.Contains("missing.lib", diagnostic.Message);
    }

    [Fact]
    public void ValidatePassesWithNoLibrariesDeclaredAtAll()
    {
        string directory = CreateTempProjectDirectory();
        GameProject project = GameProject.Load(directory);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertNotReported(diagnostics, "GBS0511");
    }

    [Fact]
    public void IncludesIsAbsentByDefault()
    {
        string directory = CreateTempProjectDirectory();
        GameProject project = GameProject.Load(directory);

        Assert.Null(project.Includes);
        Assert.Empty(project.ResolvedIncludes);
    }

    [Fact]
    public void IncludesResolveRelativeToTheProjectDirectory()
    {
        string directory = CreateTempProjectDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        File.WriteAllText(Path.Combine(directory, "native", "sram.h"), "// prototypes");

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "includes": ["native/sram.h"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.Equal(["native/sram.h"], project.Includes!);
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(directory, "native", "sram.h"))],
            project.ResolvedIncludes);
        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
    }

    [Fact]
    public void ValidateRejectsAnIncludeThatDoesNotExist()
    {
        string directory = CreateTempProjectDirectory();

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "includes": ["missing.h"] }
            """);

        GameProject project = GameProject.Load(directory);

        Assert.False(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics));
        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0512");
        Assert.Contains("missing.h", diagnostic.Message);
    }

    // GBS0513: the mapper and the save RAM have to describe the same cartridge.
    // Only a pair written explicitly can disagree: left unset, "ramBanks"
    // follows whatever mapper the build declares.

    private static GameProject ProjectWith(string json)
    {
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, GameProject.FileName), json);
        return GameProject.Load(directory);
    }

    [Fact]
    public void ABatteryWithNoRamReservedIsReported()
    {
        GameProject project = ProjectWith("""
            { "mbc": "mbc5+ram+battery", "ramBanks": 0 }
            """);

        // A warning, not an error: the ROM still builds, and the header simply
        // describes a cartridge nobody made.
        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));

        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0513");
        Assert.Contains("battery", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RamReservedForAMapperWithoutRamIsReported()
    {
        GameProject project = ProjectWith("""
            { "mbc": "mbc5", "ramBanks": 2 }
            """);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertReported(diagnostics, "GBS0513");
    }

    [Fact]
    public void AMapperAndRamThatAgreeAreAccepted()
    {
        GameProject project = ProjectWith("""
            { "mbc": "mbc5+ram+battery", "ramBanks": 1 }
            """);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertNotReported(diagnostics, "GBS0513");
    }

    /// <summary>
    /// The common case, and the reason this is not an error: saying nothing at
    /// all is not a contradiction, because the build then picks a bank count to
    /// match the mapper it declares.
    /// </summary>
    [Fact]
    public void AMapperWithNoRamBanksSettingIsSilent()
    {
        GameProject project = ProjectWith("""
            { "mbc": "mbc5+ram+battery" }
            """);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertNotReported(diagnostics, "GBS0513");
    }

    /// <summary>
    /// "none" is not checked, because a banked build overrides it: the linker
    /// needs a mapper to switch with, so reserving RAM alongside it is reachable
    /// rather than contradictory.
    /// </summary>
    [Fact]
    public void RamAlongsideAnExplicitlyAbsentMapperIsNotReported()
    {
        GameProject project = ProjectWith("""
            { "mbc": "none", "ramBanks": 1 }
            """);

        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertNotReported(diagnostics, "GBS0513");
    }

    /// <summary>
    /// The Banking sample's own settings, as a guard against the fix regressing
    /// in the file that motivated it.
    /// </summary>
    [Fact]
    public void TheBankingSampleReservesTheRamItsHeaderAdvertises()
    {
        string sample = Path.Combine(TestHarness.RepositoryRoot(), "Samples", "Banking");
        GameProject project = GameProject.Load(sample);

        Assert.Equal("mbc5+ram+battery", project.Mbc);
        Assert.True(project.RamBanks > 0, "the sample declares a battery, so it must reserve RAM");
        Assert.True(project.Validate(out IReadOnlyList<GBDiagnostic> diagnostics), TestHarness.Describe(diagnostics));
        TestHarness.AssertNotReported(diagnostics, "GBS0513");
    }

    // GBS0507: an editor's .csproj (if any) should compile the same files GB#
    // does. GB# never reads the .csproj itself, so these drive
    // CheckProjectDrift directly rather than through a full build.

    [Fact]
    public void CheckProjectDriftDoesNothingWithNoCsproj()
    {
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, "Program.cs"), "// program");

        GameProject project = GameProject.Load(directory);

        Assert.Empty(project.CheckProjectDrift());
    }

    [Fact]
    public void CheckProjectDriftIsSilentWhenTheSetsAgree()
    {
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, "Program.cs"), "// program");
        File.WriteAllText(Path.Combine(directory, "game.csproj"), "<Project />");

        GameProject project = GameProject.Load(directory);

        Assert.Empty(project.CheckProjectDrift());
    }

    [Fact]
    public void CheckProjectDriftReportsAStaleFileUnderBuild()
    {
        // MSBuild's own default glob has no idea "build" is GB#'s output
        // directory; only bin/ and obj/ are excluded by default.
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, "Program.cs"), "// program");
        File.WriteAllText(Path.Combine(directory, "game.csproj"), "<Project />");

        Directory.CreateDirectory(Path.Combine(directory, "build"));
        File.WriteAllText(Path.Combine(directory, "build", "Stale.cs"), "// previously generated");

        GameProject project = GameProject.Load(directory);

        IReadOnlyList<GBDiagnostic> diagnostics = project.CheckProjectDrift();
        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0507");

        Assert.Equal(GBSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Stale.cs", diagnostic.Message);
        Assert.Contains("game.csproj", diagnostic.Message);
        Assert.True(diagnostic.Descriptor.IsSuppressible);
    }

    [Fact]
    public void CheckProjectDriftReportsAFileExcludedByGbsharpJson()
    {
        // gbsharp.json's own "exclude" list is invisible to MSBuild's default
        // globbing, which knows nothing about it.
        string directory = CreateTempProjectDirectory();
        File.WriteAllText(Path.Combine(directory, "Program.cs"), "// program");
        File.WriteAllText(Path.Combine(directory, "game.csproj"), "<Project />");

        Directory.CreateDirectory(Path.Combine(directory, "Legacy"));
        File.WriteAllText(Path.Combine(directory, "Legacy", "OldPrototype.cs"), "// abandoned");

        File.WriteAllText(
            Path.Combine(directory, GameProject.FileName),
            """
            { "exclude": ["Legacy"] }
            """);

        GameProject project = GameProject.Load(directory);

        IReadOnlyList<GBDiagnostic> diagnostics = project.CheckProjectDrift();
        GBDiagnostic diagnostic = TestHarness.AssertReported(diagnostics, "GBS0507");

        Assert.Contains("OldPrototype.cs", diagnostic.Message);
    }
}
