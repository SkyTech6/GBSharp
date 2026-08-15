using System.Text.Json;
using System.Text.Json.Serialization;
using GBSharp.Backend.GBDK;
using GBSharp.Compiler.Diagnostics;

namespace GBSharp.Cli;

/// <summary>
/// A GB# project.
/// </summary>
/// <remarks>
/// Deliberately minimal, and deliberately not MSBuild. Thesis section 21 says
/// not to settle the project format until the compiler has shown what metadata
/// it actually needs; committing to an SDK now would fix the answer before the
/// question is understood. A project file is optional: with none, everything is
/// inferred from the directory.
/// </remarks>
public sealed class GameProject
{
    public const string FileName = "gbsharp.json";

    /// <summary>ROM name, used for the output file and the cartridge title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>"gb" or "gbc".</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>Path to an emulator executable for <c>gbsharp run</c>.</summary>
    [JsonPropertyName("emulator")]
    public string? Emulator { get; set; }

    /// <summary>Directories, relative to the project, to exclude from compilation.</summary>
    [JsonPropertyName("exclude")]
    public string[]? Exclude { get; set; }

    /// <summary>Extra directories to search for [Asset] images.</summary>
    [JsonPropertyName("assets")]
    public string[]? Assets { get; set; }

    /// <summary>
    /// The mapper: "none", "mbc1", "mbc5", "mbc5+ram" or "mbc5+ram+battery".
    /// </summary>
    /// <remarks>
    /// Only consulted when something is banked. Left unset, a banked project
    /// gets MBC5 with battery-backed RAM, which is what GBDK's own examples use
    /// and what most homebrew wants; an unbanked one declares no mapper at all.
    /// </remarks>
    [JsonPropertyName("mbc")]
    public string? Mbc { get; set; }

    /// <summary>
    /// How many 16 KB ROM banks to reserve. Unset lets the linker decide.
    /// </summary>
    [JsonPropertyName("romBanks")]
    public int? RomBanks { get; set; }

    /// <summary>How many 8 KB save RAM banks to reserve.</summary>
    [JsonPropertyName("ramBanks")]
    public int? RamBanks { get; set; }

    /// <summary>
    /// External object/library files to link into the ROM, such as a prebuilt
    /// hUGEDriver. Relative to the project directory, the same as any other
    /// relative path in this file.
    /// </summary>
    /// <remarks>
    /// GB# does not own a music engine (see <c>Audio.cs</c>'s remarks) and has
    /// no opinion on what is in these files; it only links them, the way any C
    /// toolchain links a library the developer supplies.
    /// </remarks>
    [JsonPropertyName("libraries")]
    public string[]? Libraries { get; set; }

    /// <summary>
    /// C header files to include in the generated C, so a [Native] method can
    /// call a function the framework does not wrap. Relative to the project
    /// directory, the same as any other relative path in this file.
    /// </summary>
    /// <remarks>
    /// The generated C only includes the GBDK and GB# runtime headers, and SDCC
    /// rejects a call to an undeclared function. A header named here is copied
    /// beside the generated sources and included after the runtime header, which
    /// is what lets a companion .c file under "libraries" expose functions to
    /// [Native] declarations.
    /// </remarks>
    [JsonPropertyName("includes")]
    public string[]? Includes { get; set; }

    /// <summary>
    /// How a published game presents itself: window title, size and the rest.
    /// </summary>
    /// <remarks>
    /// The settings belong to the game rather than to the player, and this is
    /// where the game says what they are. <c>gbsharp publish</c> writes them into
    /// the published executable, and the Player has no UI that could disagree.
    /// </remarks>
    [JsonPropertyName("player")]
    public PlayerSettings? Player { get; set; }

    /// <summary>
    /// Diagnostic severity, as <c>{ "GBS0201": "none" }</c> for one id or
    /// <c>{ "GBSharp.CycleCost": "none" }</c> for a whole category.
    /// </summary>
    /// <remarks>
    /// Accepts "none", "error", "warning", "performance", "resource" and "info".
    /// Wins over any <c>.editorconfig</c>, being the more specific statement, and
    /// an id wins over a category for the same reason. Diagnostics the compiler
    /// depends on stopping the build cannot be changed, and naming one by id is
    /// reported rather than ignored.
    /// </remarks>
    [JsonPropertyName("diagnostics")]
    public Dictionary<string, string>? Diagnostics { get; set; }

    [JsonIgnore]
    public string Directory { get; private set; } = string.Empty;

    /// <summary>
    /// Where an asset path is looked for, after the directory of the file that
    /// declared it.
    /// </summary>
    /// <remarks>
    /// The Assets folder is a convention rather than a requirement, and the
    /// project root is the fallback so a small game needs no folder at all.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> AssetSearchPaths =>
    [
        Path.Combine(Directory, "Assets"),
        Directory,
        .. (Assets ?? []).Select(a => Path.Combine(Directory, a)),
    ];

    /// <summary>
    /// Library/object files to link into the ROM, resolved against the
    /// project directory the same way any relative path in this file resolves.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ResolvedLibraries =>
        (Libraries ?? []).Select(l => Path.GetFullPath(Path.Combine(Directory, l))).ToList();

    /// <summary>
    /// Header files to include in the generated C, resolved against the
    /// project directory the same way any relative path in this file resolves.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ResolvedIncludes =>
        (Includes ?? []).Select(i => Path.GetFullPath(Path.Combine(Directory, i))).ToList();

    /// <summary>The machine to build for. Absent means the original Game Boy.</summary>
    /// <remarks>
    /// Only meaningful once <see cref="Validate"/> has accepted the value; an
    /// unrecognised target is rejected there rather than silently treated as DMG.
    /// </remarks>
    [JsonIgnore]
    public GBTarget ResolvedTarget =>
        TryParseTarget(Target, out GBTarget target) ? target : GBTarget.GameBoy;

    /// <summary>
    /// Parses a target name, rejecting anything that is not exactly "gb" or "gbc".
    /// </summary>
    /// <remarks>
    /// Deliberately strict. Treating an unrecognised value as the default would
    /// turn a typo into a successful build of the wrong machine: a DMG ROM from
    /// a project that asked for colour, discovered only when the palettes are
    /// missing on hardware.
    /// </remarks>
    public static bool TryParseTarget(string? value, out GBTarget target)
    {
        target = GBTarget.GameBoy;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "gb":
                target = GBTarget.GameBoy;
                return true;

            case "gbc":
                target = GBTarget.GameBoyColor;
                return true;

            default:
                return false;
        }
    }

    /// <summary>The mapper this project asks for. Validate first.</summary>
    [JsonIgnore]
    public MbcType ResolvedMbc => TryParseMbc(Mbc, out MbcType mbc) ? mbc : MbcType.None;

    /// <summary>
    /// Parses a mapper name, rejecting anything not in the supported set.
    /// </summary>
    /// <remarks>
    /// Strict for the same reason as the target: a misspelled mapper that
    /// silently became "none" would produce a cartridge that cannot switch
    /// banks, and the symptom would be a game that runs until it loads a level.
    /// </remarks>
    public static bool TryParseMbc(string? value, out MbcType mbc)
    {
        mbc = MbcType.None;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                mbc = MbcType.None;
                return true;

            case "mbc1":
                mbc = MbcType.Mbc1;
                return true;

            case "mbc5":
                mbc = MbcType.Mbc5;
                return true;

            case "mbc5+ram":
                mbc = MbcType.Mbc5Ram;
                return true;

            case "mbc5+ram+battery":
                mbc = MbcType.Mbc5RamBattery;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks the settings that have a fixed set of legal values.
    /// </summary>
    /// <returns>
    /// True if nothing here should stop the build. Diagnostics can still be
    /// reported when it returns true: a contradiction the build can proceed
    /// through is a warning, and the caller is expected to print whatever comes
    /// back rather than only reading it on failure.
    /// </returns>
    public bool Validate(out IReadOnlyList<GBDiagnostic> diagnostics)
    {
        var bag = new DiagnosticBag();
        string path = Path.Combine(Directory, FileName);

        if (!TryParseTarget(Target, out _))
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"'{Target}' is not a target. Use \"gb\" for the original Game Boy or \"gbc\" for Game Boy Color.");
        }

        if (!TryParseMbc(Mbc, out _))
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"'{Mbc}' is not a mapper. Use \"none\", \"mbc1\", \"mbc5\", \"mbc5+ram\" or \"mbc5+ram+battery\".");
        }

        if (RomBanks is { } romBanks && romBanks is < 2 or > 512)
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"\"romBanks\" is {romBanks}. A cartridge has between 2 and 512 banks, counting bank 0.");
        }

        if (RamBanks is { } ramBanks && ramBanks is < 0 or > 16)
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"\"ramBanks\" is {ramBanks}. A cartridge has at most 16 banks of save RAM.");
        }

        // Only checked when the mapper was named explicitly. Left unset, the
        // build picks the RAM bank count to match whatever mapper it defaults
        // to, so there is no contradiction to report; reaching this needs both
        // settings written, disagreeing with each other.
        if (RamBanks is { } banks && !string.IsNullOrWhiteSpace(Mbc) && TryParseMbc(Mbc, out MbcType named))
        {
            if (named.DeclaresRam() && banks == 0)
            {
                bag.Report(
                    GBDiagnostics.CartridgeRamMismatch,
                    SourceSpan.None,
                    path,
                    $"\"mbc\" is \"{Mbc}\", which puts save RAM on the cartridge, but \"ramBanks\" " +
                    "is 0. The header will advertise a battery with nothing behind it.");
            }
            // "none" is left out on purpose: a banked build needs a mapper to
            // switch with, so the builder overrides it, and the RAM reserved
            // here is reachable after all rather than contradictory.
            else if (!named.DeclaresRam() && named != MbcType.None && banks > 0)
            {
                bag.Report(
                    GBDiagnostics.CartridgeRamMismatch,
                    SourceSpan.None,
                    path,
                    $"\"ramBanks\" is {banks}, but \"mbc\" is \"{Mbc}\", which has no save RAM " +
                    "for the game to reach.");
            }
        }

        // Caught here rather than in the Player, which reads these long after
        // anyone could do anything about them: a window 40 screens wide is a
        // typo in a project file, and the person who can fix it is the one
        // running this command.
        if (Player?.Scale is { } scale && scale is < 1 or > 8)
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"\"player.scale\" is {scale}. A window is 1 to 8 times the size of the " +
                "Game Boy's 160x144 screen.");
        }

        if (Player?.Volume is { } volume && volume is < 0 or > 100)
        {
            bag.Report(
                GBDiagnostics.ProjectFileInvalid,
                SourceSpan.None,
                path,
                $"\"player.volume\" is {volume}. Volume runs from 0 to 100.");
        }

        if (Libraries is not null)
        {
            // Checked here rather than left to RomBuilder: by the time a build
            // reaches the linker, silently proceeding without a library the
            // developer thought they linked is a worse failure than a clear
            // upfront error, and Directory is already fully resolved.
            foreach (string library in Libraries)
            {
                string resolved = Path.GetFullPath(Path.Combine(Directory, library));
                if (!File.Exists(resolved))
                {
                    bag.Report(
                        GBDiagnostics.LibraryNotFound,
                        SourceSpan.None,
                        path,
                        library);
                }
            }
        }

        if (Includes is not null)
        {
            // Same reasoning as "libraries": the developer believes the header
            // is part of the build, so a missing file is a clear upfront error
            // rather than an implicit-declaration failure deep inside SDCC.
            foreach (string include in Includes)
            {
                string resolved = Path.GetFullPath(Path.Combine(Directory, include));
                if (!File.Exists(resolved))
                {
                    bag.Report(
                        GBDiagnostics.IncludeNotFound,
                        SourceSpan.None,
                        path,
                        include);
                }
            }
        }

        if (Diagnostics is not null)
        {
            var known = GBDiagnostics.All.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach ((string key, string severity) in Diagnostics)
            {
                // A setting for something that does not exist is almost always a
                // typo, and silently ignoring it leaves the developer believing
                // they configured something.
                if (!known.Contains(key) && !GBDiagnosticOptions.TryParseCategory(key, out _))
                {
                    bag.Report(
                        GBDiagnostics.ProjectFileInvalid,
                        SourceSpan.None,
                        path,
                        $"\"diagnostics\" names '{key}', which is not a GB# diagnostic or category.");
                }
                else if (!GBDiagnosticOptions.TryParseSeverity(severity, out _))
                {
                    bag.Report(
                        GBDiagnostics.ProjectFileInvalid,
                        SourceSpan.None,
                        path,
                        $"'{severity}' is not a severity for {key}. Use \"none\", \"error\", " +
                        "\"warning\", \"performance\", \"resource\" or \"info\".");
                }
            }
        }

        diagnostics = bag.Diagnostics;
        return !diagnostics.Any(d => d.IsError);
    }

    [JsonIgnore]
    public string ResolvedName =>
        string.IsNullOrWhiteSpace(Name) ? new DirectoryInfo(Directory).Name : Name;

    /// <summary>
    /// Loads the project in <paramref name="directory"/>, or synthesises one.
    /// </summary>
    public static GameProject Load(string directory)
    {
        string path = Path.Combine(directory, FileName);

        GameProject project;
        if (File.Exists(path))
        {
            project = JsonSerializer.Deserialize<GameProject>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? new GameProject();
        }
        else
        {
            project = new GameProject();
        }

        project.Directory = Path.GetFullPath(directory);
        return project;
    }

    /// <summary>
    /// Every .cs file in the project, excluding build output and anything the
    /// project file lists.
    /// </summary>
    public IReadOnlyList<string> EnumerateSourceFiles()
    {
        var excluded = new List<string> { "bin", "obj", "build" };
        if (Exclude is not null)
        {
            excluded.AddRange(Exclude);
        }

        return System.IO.Directory
            .EnumerateFiles(Directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(path, excluded))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private bool IsExcluded(string path, IEnumerable<string> excluded)
    {
        string relative = Path.GetRelativePath(Directory, path);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => excluded.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Compares the compile set an editor's .csproj would produce against
    /// <see cref="EnumerateSourceFiles"/>, and reports where they disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>gbsharp new</c> writes a .csproj so an editor can bind and analyse the
    /// code (see GBSharp.Sdk); <c>gbsharp build</c> never reads it and always
    /// uses <see cref="EnumerateSourceFiles"/> instead. A wrong .csproj compile
    /// set therefore cannot produce a wrong ROM, which is why this is a warning
    /// rather than a build error, and does nothing at all when no .csproj
    /// exists to drift from.
    /// </para>
    /// <para>
    /// MSBuild's own default for an SDK-style project is <c>**/*.cs</c>,
    /// excluding anything under a bin/ or obj/ segment. That default has no
    /// idea about gbsharp.json's own "exclude" list or about a "build" folder
    /// full of previously generated C#, so the two views of "the files in this
    /// project" can end up disagreeing without either file having a mistake in
    /// it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<GBDiagnostic> CheckProjectDrift()
    {
        string? csproj = System.IO.Directory
            .EnumerateFiles(Directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

        if (csproj is null)
        {
            return [];
        }

        var msBuildDefaultExcluded = new[] { "bin", "obj" };

        var msBuildSet = System.IO.Directory
            .EnumerateFiles(Directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(path, msBuildDefaultExcluded))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var gbSharpSet = EnumerateSourceFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> extraInCsproj = msBuildSet
            .Where(path => !gbSharpSet.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(RelativeToProject)
            .ToList();

        List<string> missingFromCsproj = gbSharpSet
            .Where(path => !msBuildSet.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(RelativeToProject)
            .ToList();

        if (extraInCsproj.Count == 0 && missingFromCsproj.Count == 0)
        {
            return [];
        }

        var parts = new List<string>();
        if (extraInCsproj.Count > 0)
        {
            parts.Add("extra in the .csproj: " + string.Join(", ", extraInCsproj));
        }

        if (missingFromCsproj.Count > 0)
        {
            parts.Add("missing from the .csproj: " + string.Join(", ", missingFromCsproj));
        }

        var bag = new DiagnosticBag();
        bag.Report(
            GBDiagnostics.ProjectDrift,
            SourceSpan.None,
            Path.GetFileName(csproj),
            string.Join("; ", parts));

        return bag.Diagnostics;
    }

    private string RelativeToProject(string path) => Path.GetRelativePath(Directory, path);
}
