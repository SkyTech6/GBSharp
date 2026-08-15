using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using GBSharp.Assets.Pipeline;
using GBSharp.Cli.Emulators;
using GBSharp.Cli.Publishing;
using GBSharp.Cli.Reporting;
using GBSharp.Backend.GBDK;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Backend.GBDK.Toolchain;
using GBSharp.Cli;
using GBSharp.Compiler;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using GBSharp.Emulator;

var pathArgument = new Argument<string>("path")
{
    Description = "The project directory. Defaults to the current directory.",
    DefaultValueFactory = _ => ".",
};

var emitCOption = new Option<bool>("--emit-c")
{
    Description = "Keep the generated C next to the ROM so it can be inspected.",
};

var emitIrOption = new Option<bool>("--emit-ir")
{
    Description = "Write the GB# intermediate representation alongside the ROM.",
};

var annotateSourceOption = new Option<bool>("--annotate-source")
{
    Description = "Comment every generated C statement with the C# line that produced it, " +
                   "and write sourcemap.json alongside the generated C.",
};

var outOption = new Option<string?>("--out", "-o")
{
    Description = "Output directory. Defaults to <project>/build.",
};

var targetOption = new Option<string?>("--target")
{
    Description = "Override the project target: gb or gbc.",
};

var gbdkOption = new Option<string?>("--gbdk-path")
{
    Description = "GBDK-2020 install root. Overrides GBDK_HOME and the vendored copy.",
};

var reportJsonOption = new Option<string?>("--report-json")
{
    Description = "Write the build report as JSON. Defaults to <out>/report.json.",
    Arity = ArgumentArity.ZeroOrOne,
};

var buildCommand = new Command("build", "Compile a GB# project into a ROM.");
buildCommand.Add(pathArgument);
buildCommand.Add(emitCOption);
buildCommand.Add(emitIrOption);
buildCommand.Add(annotateSourceOption);
buildCommand.Add(outOption);
buildCommand.Add(targetOption);
buildCommand.Add(gbdkOption);
buildCommand.Add(reportJsonOption);
buildCommand.SetAction(parseResult => Build(
    parseResult.GetValue(pathArgument)!,
    parseResult.GetValue(emitCOption),
    parseResult.GetValue(emitIrOption),
    parseResult.GetValue(annotateSourceOption),
    parseResult.GetValue(outOption),
    parseResult.GetValue(targetOption),
    parseResult.GetValue(gbdkOption),
    launch: false,
    reportJson: parseResult.GetResult(reportJsonOption) is null
        ? null
        : parseResult.GetValue(reportJsonOption) ?? string.Empty));

// The bundled Player runs a game. The emulators in the catalog debug one, and
// they load the .sym GB# writes, so naming one here is a first-class choice
// rather than a workaround.
var emulatorOption = new Option<string?>("--emulator")
{
    Description = "Which emulator to launch: \"" + EmulatorLocator.BundledPlayerId +
                  "\" for the bundled GB# Player, " +
                  string.Join(", ", EmulatorCatalog.Known.Select(e => $"\"{e.Id}\"")) +
                  ", or a path to an executable.",
};

var runCommand = new Command("run", "Build a ROM and launch it.");
runCommand.Add(pathArgument);
runCommand.Add(emitCOption);
runCommand.Add(outOption);
runCommand.Add(targetOption);
runCommand.Add(gbdkOption);
runCommand.Add(emulatorOption);
runCommand.SetAction(parseResult => Build(
    parseResult.GetValue(pathArgument)!,
    parseResult.GetValue(emitCOption),
    emitIr: false,
    annotateSource: false,
    parseResult.GetValue(outOption),
    parseResult.GetValue(targetOption),
    parseResult.GetValue(gbdkOption),
    launch: true,
    reportJson: null,
    emulator: parseResult.GetValue(emulatorOption)));

var ridArgument = new Argument<string>("rid")
{
    Description = "The platform to publish for: " + string.Join(", ", PlayerStub.SupportedRids) + ".",
    DefaultValueFactory = _ => PlayerStub.HostRid,
};

var singleFileOption = new Option<bool>("--single-file")
{
    Description = "Web only: inline the runtime and the ROM into one .html that " +
                  "opens without a server.",
};

var publishCommand = new Command(
    "publish",
    "Build a ROM and wrap it in a standalone game that runs without an emulator.");
publishCommand.Add(ridArgument);
publishCommand.Add(pathArgument);
publishCommand.Add(outOption);
publishCommand.Add(targetOption);
publishCommand.Add(gbdkOption);
publishCommand.Add(singleFileOption);
publishCommand.SetAction(parseResult => Build(
    parseResult.GetValue(pathArgument)!,
    emitC: false,
    emitIr: false,
    annotateSource: false,
    parseResult.GetValue(outOption),
    parseResult.GetValue(targetOption),
    parseResult.GetValue(gbdkOption),
    launch: false,
    reportJson: null,
    publishRid: parseResult.GetValue(ridArgument),
    singleFile: parseResult.GetValue(singleFileOption)));

var framesOption = new Option<int>("--frames")
{
    Description = "Frames to run. 60 is one second of emulated time.",
    DefaultValueFactory = _ => 600,
};

var profileCommand = new Command(
    "profile",
    "Build a ROM, run it headlessly, and report where the frame budget went.");
profileCommand.Add(pathArgument);
profileCommand.Add(outOption);
profileCommand.Add(targetOption);
profileCommand.Add(gbdkOption);
profileCommand.Add(framesOption);
profileCommand.SetAction(parseResult => Build(
    parseResult.GetValue(pathArgument)!,
    emitC: false,
    emitIr: false,
    annotateSource: false,
    parseResult.GetValue(outOption),
    parseResult.GetValue(targetOption),
    parseResult.GetValue(gbdkOption),
    launch: false,
    reportJson: null,
    profileFrames: Math.Max(parseResult.GetValue(framesOption), 1)));

var cleanCommand = new Command("clean", "Delete a project's build output.");
cleanCommand.Add(pathArgument);
cleanCommand.Add(outOption);
cleanCommand.SetAction(parseResult => Clean(
    parseResult.GetValue(pathArgument)!,
    parseResult.GetValue(outOption)));

var fixOption = new Option<bool>("--fix")
{
    Description = "Fetch anything missing (GBDK, the emulator runtime) into the per-user cache, " +
                  "verified against the pinned lock files.",
};

var doctorCommand = new Command("doctor", "Report the state of the GB# toolchain.");
doctorCommand.Add(gbdkOption);
doctorCommand.Add(fixOption);
doctorCommand.SetAction(parseResult => Doctor(
    parseResult.GetValue(gbdkOption),
    parseResult.GetValue(fixOption)));

// Everything up to and including asset conversion runs without GBDK, so these
// two work on a bare checkout. That matters for CI, where a lint job should not
// have to install a C toolchain to find out a project uses List<T>.
var analyzeCommand = new Command("analyze", "Check a project without building a ROM. Needs no toolchain.");
analyzeCommand.Add(pathArgument);
analyzeCommand.Add(targetOption);
analyzeCommand.SetAction(parseResult => Analyze(
    parseResult.GetValue(pathArgument)!,
    parseResult.GetValue(targetOption),
    assetsOnly: false));

var assetsCommand = new Command("assets", "Convert a project's assets and report what they cost.");
assetsCommand.Add(pathArgument);
assetsCommand.Add(targetOption);
assetsCommand.SetAction(parseResult => Analyze(
    parseResult.GetValue(pathArgument)!,
    parseResult.GetValue(targetOption),
    assetsOnly: true));

var nameArgument = new Argument<string>("name")
{
    Description = "The project name. Also the ROM name and the cartridge title.",
};

var templateOption = new Option<string>("--template", "-t")
{
    Description = "empty, sprite or background.",
    DefaultValueFactory = _ => "empty",
};

var forceOption = new Option<bool>("--force")
{
    Description = "Write into a directory that is not empty.",
};

var newCommand = new Command("new", "Create a GB# project.");
newCommand.Add(nameArgument);
newCommand.Add(templateOption);
newCommand.Add(targetOption);
newCommand.Add(outOption);
newCommand.Add(forceOption);
newCommand.SetAction(parseResult => New(
    parseResult.GetValue(nameArgument)!,
    parseResult.GetValue(templateOption)!,
    parseResult.GetValue(targetOption),
    parseResult.GetValue(outOption),
    parseResult.GetValue(forceOption)));

var root = new RootCommand("GB# - a statically compiled C# development environment for the Game Boy.");
root.Add(newCommand);
root.Add(buildCommand);
root.Add(runCommand);
root.Add(profileCommand);
root.Add(publishCommand);
root.Add(cleanCommand);
root.Add(analyzeCommand);
root.Add(assetsCommand);
root.Add(doctorCommand);

return root.Parse(args).Invoke();

// ---------------------------------------------------------------------------

/// <summary>
/// Writes a new project from a template.
/// </summary>
/// <remarks>
/// Refuses a directory that already has files in it unless told otherwise: the
/// one thing this command must never do is overwrite work.
/// </remarks>
static int New(string name, string template, string? target, string? outputDirectory, bool force)
{
    if (!GameProject.TryParseTarget(target, out GBTarget parsed))
    {
        Console.Error.WriteLine(
            $"error: '{target}' is not a target. Use \"gb\" for the original Game Boy or \"gbc\" for Game Boy Color.");
        return 1;
    }

    TemplateContent? content = Templates.Create(
        template,
        name,
        parsed == GBTarget.GameBoyColor ? "gbc" : "gb",
        ResolveCliProjectDirectory());

    if (content is null)
    {
        Console.Error.WriteLine($"error: '{template}' is not a template. Available: {string.Join(", ", Templates.Names)}.");
        return 1;
    }

    string directory = Path.GetFullPath(outputDirectory is { Length: > 0 } ? outputDirectory : name);

    if (Directory.Exists(directory) && !force &&
        (Directory.EnumerateFiles(directory).Any() || Directory.EnumerateDirectories(directory).Any()))
    {
        Console.Error.WriteLine($"error: '{directory}' is not empty. Pass --force to write into it anyway.");
        return 1;
    }

    Directory.CreateDirectory(directory);

    foreach ((string relative, string text) in content.Files)
    {
        Write(relative, () => File.WriteAllText(Path.Combine(directory, relative), text));
    }

    foreach ((string relative, byte[] bytes) in content.Binaries)
    {
        Write(relative, () => File.WriteAllBytes(Path.Combine(directory, relative), bytes));
    }

    Console.WriteLine();
    Console.WriteLine($"Created {name} in {directory}");
    Console.WriteLine();
    Console.WriteLine($"  gbsharp build {Path.GetFileName(directory)}");

    return 0;

    void Write(string relative, Action write)
    {
        string full = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        write();
        Console.WriteLine($"  {relative}");
    }
}

/// <summary>
/// Compiles and reports, without producing a ROM.
/// </summary>
/// <remarks>
/// This is <c>build</c> minus the backend. Everything it needs (parsing,
/// validation, lowering, asset conversion) is in managed code, so it runs with
/// no GBDK installed. That is the whole point: it makes a fast CI lint job
/// possible, and gives an artist working on a PNG a loop that does not involve a
/// C compiler.
/// </remarks>
static int Analyze(string path, string? targetOverride, bool assetsOnly)
{
    if (!TryLoadProject(path, targetOverride, out GameProject? project, out IReadOnlyList<string> sources))
    {
        return 1;
    }

    CompilationResult result = new GBSharpCompiler().Compile(CreateRequest(project!, sources));

    ConsoleReporter.WriteDiagnostics(result.Diagnostics);

    if (!result.Succeeded || result.Module is null)
    {
        Console.Error.WriteLine(assetsOnly ? "Asset conversion failed." : "Analysis failed.");
        return 1;
    }

    if (assetsOnly)
    {
        ConsoleReporter.WriteAssetReport(result.Module);
    }
    else
    {
        Console.WriteLine($"No problems found in {sources.Count} file{(sources.Count == 1 ? string.Empty : "s")}.");
    }

    return 0;
}

/// <summary>
/// Loads and validates a project, and finds its sources.
/// </summary>
/// <remarks>
/// Shared by every command that compiles, so the target override goes through
/// the same validation everywhere rather than only where someone remembered.
/// </remarks>
static bool TryLoadProject(
    string path,
    string? targetOverride,
    out GameProject? project,
    out IReadOnlyList<string> sources)
{
    project = null;
    sources = [];

    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"error: project directory '{path}' does not exist.");
        return false;
    }

    GameProject loaded = GameProject.Load(path);
    if (targetOverride is { Length: > 0 })
    {
        // The override goes through the project's own settings so it is
        // validated by the same rule, rather than being trusted because it came
        // from a command line.
        loaded.Target = targetOverride;
    }

    // Written unconditionally: validation reports warnings the build can
    // proceed through, and reading them only on failure would drop them.
    bool projectIsBuildable = loaded.Validate(out IReadOnlyList<GBDiagnostic> projectDiagnostics);
    ConsoleReporter.WriteDiagnostics(projectDiagnostics);

    if (!projectIsBuildable)
    {
        return false;
    }

    // Informational only: GB# always builds from EnumerateSourceFiles below,
    // never from the .csproj, so drift between the two cannot fail a build.
    ConsoleReporter.WriteDiagnostics(loaded.CheckProjectDrift());

    IReadOnlyList<string> found = loaded.EnumerateSourceFiles();
    if (found.Count == 0)
    {
        Console.Error.WriteLine($"error: GBS0504: no .cs files found under '{loaded.Directory}'.");
        return false;
    }

    project = loaded;
    sources = found;
    return true;
}

static CompilationRequest CreateRequest(GameProject project, IReadOnlyList<string> sources) =>
    new(project.ResolvedName, sources, FrameworkAssemblyPath())
    {
        AssetCompiler = new PngAssetCompiler(),
        AssetSearchPaths = project.AssetSearchPaths,
        AssetProfile = project.ResolvedTarget == GBTarget.GameBoyColor
            ? AssetTargetProfile.GameBoyColor
            : AssetTargetProfile.GameBoy,
        DiagnosticOptions = DiagnosticConfiguration.Read(
            project.Diagnostics,
            DiagnosticConfiguration.FindConfigFiles(project.Directory),
            sources[0]),
    };

static int Build(
    string path,
    bool emitC,
    bool emitIr,
    bool annotateSource,
    string? outputDirectory,
    string? targetOverride,
    string? gbdkPath,
    bool launch,
    string? reportJson,
    string? publishRid = null,
    string? emulator = null,
    bool singleFile = false,
    int profileFrames = 0)
{
    if (!TryLoadProject(path, targetOverride, out GameProject? loaded, out IReadOnlyList<string> sources))
    {
        return 1;
    }

    GameProject project = loaded!;

    string buildDirectory = outputDirectory is { Length: > 0 }
        ? Path.GetFullPath(outputDirectory)
        : Path.Combine(project.Directory, "build");

    Console.WriteLine($"Parsing C#...        {sources.Count} file{(sources.Count == 1 ? string.Empty : "s")}");

    Console.WriteLine("Validating GB#...");
    CompilationResult result = new GBSharpCompiler().Compile(CreateRequest(project, sources));

    ConsoleReporter.WriteDiagnostics(result.Diagnostics);

    if (!result.Succeeded || result.Module is null)
    {
        Console.Error.WriteLine("Build failed.");
        return 1;
    }

    Console.WriteLine("Lowering...");

    if (emitIr)
    {
        Directory.CreateDirectory(buildDirectory);
        string irPath = Path.Combine(buildDirectory, project.ResolvedName + ".gbir");
        File.WriteAllText(irPath, IRPrinter.Print(result.Module));
        Console.WriteLine($"  IR written to {irPath}");
    }

    Console.WriteLine("Generating C...");
    Console.WriteLine("Compiling with GBDK...");

    var options = new RomBuildOptions(buildDirectory, project.ResolvedTarget, emitC, gbdkPath)
    {
        Cartridge = project.ResolvedMbc,
        RomBankCount = project.RomBanks,
        RamBankCount = project.RamBanks,
        Libraries = project.ResolvedLibraries,
        Includes = project.ResolvedIncludes,
        AnnotateSource = annotateSource,
    };
    RomBuildResult build = new RomBuilder().Build(result.Module, options);

    ConsoleReporter.WriteDiagnostics(build.Diagnostics);

    if (!build.Succeeded || build.RomPath is null)
    {
        Console.Error.WriteLine("Build failed.");
        return 1;
    }

    Console.WriteLine("Linking ROM...");
    Console.WriteLine();

    var allDiagnostics = result.Diagnostics.Concat(build.Diagnostics).ToList();

    // Built once and rendered twice. The terminal used to compute its own
    // figures alongside this, and the two had already drifted.
    GbdkToolchain.TryLocate(gbdkPath, out GbdkToolchain? toolchain, out _);

    var report = BuildReport.Create(
        result.Module,
        build.RomPath,
        project.ResolvedTarget,
        allDiagnostics,
        build.Usage,
        toolchain?.Version);

    ConsoleReporter.WriteBuildReport(
        report, result.Module, build.RomPath, project.ResolvedTarget, allDiagnostics, build.Usage);

    if (reportJson is not null)
    {
        string reportPath = reportJson.Length > 0
            ? Path.GetFullPath(reportJson)
            : Path.Combine(buildDirectory, "report.json");

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, BuildReportJson.Default.BuildReport));

        Console.WriteLine($"Report: {reportPath}");
    }

    if (build.GeneratedCDirectory is not null)
    {
        int count = build.GeneratedFiles.Count;
        Console.WriteLine($"Generated C: {build.GeneratedCDirectory}  ({count} file{(count == 1 ? "" : "s")})");
    }

    // A budget can only be checked once the ROM exists, so the ROM is written
    // and then the build fails. Keeping the file is deliberate: the report above
    // is how a developer finds the bytes to remove.
    if (build.Diagnostics.Any(d => d.IsError))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Build failed: a declared budget was exceeded.");
        return 1;
    }

    if (publishRid is { Length: > 0 })
    {
        return Publish(project, build.RomPath, publishRid, outputDirectory, singleFile);
    }

    if (profileFrames > 0)
    {
        return Profile(build.RomPath, profileFrames);
    }

    return launch ? Launch(project, build.RomPath, emulator) : 0;
}

/// <summary>
/// Runs a ROM headlessly with the profiler on and reports where its frame
/// budget went, in the developer's own methods.
/// </summary>
/// <remarks>
/// <para>
/// The measured counterpart of the cycle estimates the build report already
/// prints. The estimate says what a method should cost from a walk over the IR;
/// this says what it did cost on the hardware, and the two disagreeing is
/// information rather than a bug in either.
/// </para>
/// <para>
/// Needs the instrumented flavour of the runtime, which the profiler lives in.
/// Nothing else in the CLI does, so this is also the only command that says so
/// when it is missing.
/// </para>
/// </remarks>
static int Profile(string romPath, int frames)
{
    Console.WriteLine();
    Console.WriteLine($"Profiling {frames} frames...");

    if (!EmulatorRuntime.IsAvailable)
    {
        Console.Error.WriteLine(
            "The emulator runtime was not found. Run 'gbsharp doctor --fix' to fetch it.");
        return 1;
    }

    try
    {
        EmulatorRuntime.Load(EmulatorFlavour.Debug);
    }
    catch (InvalidOperationException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }

    using GameBoy game = GameBoy.Load(File.ReadAllBytes(romPath));

    if (!GameBoy.SetProfilingEnabled(true))
    {
        Console.Error.WriteLine(
            "This runtime has no instrumentation, so there is nothing to profile with.");
        return 1;
    }

    try
    {
        GameBoy.ClearProfile();
        game.RunFrames(frames);

        var counts = new uint[game.RomSize];
        var cycles = new uint[game.RomSize];
        game.ReadProfile(counts, cycles);

        RomSymbolResolver resolver = RomSymbolResolver.ForRom(romPath);

        Console.WriteLine();
        Console.Write(ProfileReport.Build(resolver, counts, cycles, frames).Describe());

        // The same run answers both questions, and they are complementary: the
        // profile says what the expensive code was, coverage says what code
        // this run never reached and so proved nothing about.
        var usage = new RomUsage[game.RomSize];
        if (game.ReadRomUsage(usage) > 0)
        {
            Console.WriteLine();
            Console.Write(CoverageReport
                .Build(resolver, System.Runtime.InteropServices.MemoryMarshal.AsBytes<RomUsage>(usage))
                .Describe());
        }
    }
    finally
    {
        GameBoy.SetProfilingEnabled(false);
    }

    return 0;
}

/// <summary>
/// Wraps a built ROM in a standalone game.
/// </summary>
/// <remarks>
/// <para>
/// The person publishing has no C toolchain, so this cannot link. It copies the
/// prebuilt Player for the target platform and appends the ROM and the window
/// settings to it; the Player reads them out of its own file at startup. That is
/// why a game can be published for a platform this machine could not compile
/// for, and why publishing takes about as long as copying a file.
/// </para>
/// <para>
/// Signing, when it happens, has to happen after this: the appended bytes are
/// part of what a signature covers.
/// </para>
/// </remarks>
static int Publish(
    GameProject project, string romPath, string rid, string? outputDirectory, bool singleFile)
{
    Console.WriteLine();
    Console.WriteLine($"Publishing for {rid}...");

    string publishDirectory = outputDirectory is { Length: > 0 }
        ? Path.GetFullPath(outputDirectory)
        : Path.Combine(project.Directory, "publish", rid);

    if (rid == WebPublisher.Rid)
    {
        return PublishWeb(project, romPath, publishDirectory, singleFile);
    }

    string? stub = PlayerStub.Resolve(rid, Console.WriteLine, out string? error);
    if (stub is null)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(error);
        return 1;
    }

    string extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
    string gamePath = Path.Combine(publishDirectory, project.ResolvedName + extension);

    string config = PlayerSettings.Serialize(project.Player, project.ResolvedName);

    try
    {
        GamePayload.Write(stub, gamePath, File.ReadAllBytes(romPath), config);
    }
    catch (IOException e)
    {
        Console.Error.WriteLine($"The game could not be written to {gamePath}: {e.Message}");
        return 1;
    }

    // The executable bit does not survive an archive on every platform, and a
    // game nobody can run is not published.
    if (extension.Length == 0 && !OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            gamePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    var companions = new List<string>();
    foreach (string companion in PlayerStub.RuntimeCompanions(stub))
    {
        string destination = Path.Combine(publishDirectory, Path.GetFileName(companion));
        File.Copy(companion, destination, overwrite: true);
        companions.Add(Path.GetFileName(companion));
    }

    var info = new FileInfo(gamePath);
    Console.WriteLine();
    Console.WriteLine($"Published: {gamePath}");
    Console.WriteLine($"  {info.Length / 1024.0:F1} KB, opens straight into the game");

    if (companions.Count > 0)
    {
        Console.WriteLine($"  ships alongside: {string.Join(", ", companions)}");
    }

    return 0;
}

/// <summary>
/// Publishes a game as a web page.
/// </summary>
/// <remarks>
/// The same ROM and settings as any other target, over the same ABI, with a
/// browser standing in for the window and the sound device.
/// </remarks>
static int PublishWeb(GameProject project, string romPath, string publishDirectory, bool singleFile)
{
    string? runtime = PlayerStub.ResolveWebRuntime(Console.WriteLine, out string? error);
    if (runtime is null)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(error);
        return 1;
    }

    string assets = Path.Combine(AppContext.BaseDirectory, "WebPlayer");
    if (!File.Exists(Path.Combine(assets, "index.html")))
    {
        Console.Error.WriteLine($"The web player's assets are missing from {assets}.");
        return 1;
    }

    string title = project.Player?.Title is { Length: > 0 } named ? named : project.ResolvedName;

    string page;
    try
    {
        page = WebPublisher.Write(
            runtime,
            assets,
            publishDirectory,
            File.ReadAllBytes(romPath),
            title,
            PlayerSettings.Serialize(project.Player, project.ResolvedName),
            singleFile);
    }
    catch (IOException e)
    {
        Console.Error.WriteLine($"The game could not be written to {publishDirectory}: {e.Message}");
        return 1;
    }

    var info = new FileInfo(page);
    Console.WriteLine();
    Console.WriteLine($"Published: {page}");

    if (singleFile)
    {
        Console.WriteLine($"  {info.Length / 1024.0:F1} KB, one file, opens without a server");
    }
    else
    {
        Console.WriteLine("  a folder to upload to any static host");
        Console.WriteLine("  browsers block module scripts on file://, so serve it over http to try it locally");
    }

    return 0;
}

static int Launch(GameProject project, string romPath, string? commandLine = null)
{
    if (!EmulatorLocator.TryResolve(
            project.Emulator,
            commandLine,
            romPath,
            out ResolvedEmulator? emulator,
            out IReadOnlyList<string> searched) ||
        emulator is null)
    {
        // A missing emulator has never failed a build and still does not: the ROM
        // is the deliverable, and running it is a convenience.
        Console.WriteLine();
        ConsoleReporter.WriteDiagnostic(new GBDiagnostic(
            GBDiagnostics.EmulatorNotConfigured,
            GBDiagnostics.EmulatorNotConfigured.MessageFormat,
            SourceSpan.None,
            GBSeverity.Warning));

        if (searched.Count > 0)
        {
            Console.WriteLine($"  Looked for: {string.Join(", ", EmulatorCatalog.Known.Select(e => e.DisplayName))}");
        }

        Console.WriteLine($"The ROM is ready at {romPath}.");
        return 0;
    }

    // UseShellExecute = false with an explicit argument list, so a path
    // containing a space survives and a launch failure is catchable.
    var startInfo = new ProcessStartInfo(emulator.Executable)
    {
        UseShellExecute = false,
    };

    foreach (string argument in emulator.Arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        Process.Start(startInfo);
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
    {
        ConsoleReporter.WriteDiagnostic(new GBDiagnostic(
            GBDiagnostics.EmulatorLaunchFailed,
            string.Format(GBDiagnostics.EmulatorLaunchFailed.MessageFormat, emulator.Executable, e.Message),
            SourceSpan.None,
            GBSeverity.Error));

        return 1;
    }

    Console.WriteLine($"Launched {emulator.Describe}");

    if (emulator.LoadsSymbolsAutomatically)
    {
        // The .sym is already beside the ROM, and these emulators find it
        // themselves. Worth saying, because otherwise nobody knows.
        Console.WriteLine("  Symbols alongside the ROM will be picked up for source-level debugging.");
    }

    return 0;
}

/// <summary>
/// Whether <c>gbsharp run</c> would find something to launch.
/// </summary>
/// <remarks>
/// Resolved against a made-up ROM path, because the question is which emulator
/// exists rather than whether a particular ROM does.
/// </remarks>
static void WriteEmulatorRow()
{
    bool found = EmulatorLocator.TryResolve(
        projectSetting: null,
        commandLine: null,
        romPath: Path.Combine(Path.GetTempPath(), "probe.gb"),
        out ResolvedEmulator? emulator,
        out _);

    string describe = found && emulator is not null
        ? $"{emulator.Describe} ({emulator.Executable})"
        : "none found; set \"emulator\" in the project file or GBSHARP_EMULATOR";

    Console.WriteLine($"{"Emulator".PadRight(24)}{describe}");
}

static int Clean(string path, string? outputDirectory)
{
    GameProject project = GameProject.Load(path);

    string buildDirectory = outputDirectory is { Length: > 0 }
        ? Path.GetFullPath(outputDirectory)
        : Path.Combine(project.Directory, "build");

    if (!Directory.Exists(buildDirectory))
    {
        Console.WriteLine($"Nothing to clean: {buildDirectory} does not exist.");
        return 0;
    }

    Directory.Delete(buildDirectory, recursive: true);
    Console.WriteLine($"Removed {buildDirectory}");
    return 0;
}

static int Doctor(string? gbdkPath, bool fix)
{
    Console.WriteLine("GB# doctor");
    Console.WriteLine("────────────────────────────────");
    Console.WriteLine();

    if (fix && !Fix(gbdkPath))
    {
        return 1;
    }

    Console.WriteLine($"{"GB# version".PadRight(24)}{typeof(GBSharpCompiler).Assembly.GetName().Version}");
    Console.WriteLine($"{".NET runtime".PadRight(24)}{Environment.Version}");

    string frameworkPath = FrameworkAssemblyPath();
    Console.WriteLine($"{"Framework assembly".PadRight(24)}{(File.Exists(frameworkPath) ? frameworkPath : "MISSING")}");

    if (GbdkToolchain.TryLocate(gbdkPath, out GbdkToolchain? toolchain, out IReadOnlyList<string> searched) &&
        toolchain is not null)
    {
        Console.WriteLine($"{"GBDK root".PadRight(24)}{toolchain.Root}");
        Console.WriteLine($"{"GBDK version".PadRight(24)}{toolchain.Version ?? "unknown (not installed by tools/get-gbdk.ps1)"}");
        Console.WriteLine($"{"Compiler driver".PadRight(24)}{toolchain.CompilerDriver}");
        WriteEmulatorRow();
        Console.WriteLine();
        Console.WriteLine("Ready to build.");
        return 0;
    }

    Console.WriteLine($"{"GBDK root".PadRight(24)}NOT FOUND");
    Console.WriteLine();
    Console.WriteLine("Searched:");
    foreach (string location in searched.Take(6))
    {
        Console.WriteLine($"  {location}");
    }

    Console.WriteLine();
    Console.WriteLine("Run 'gbsharp doctor --fix' to fetch the pinned toolchain,");
    Console.WriteLine("or set GBDK_HOME to an existing GBDK-2020 installation.");
    return 1;
}

/// <summary>
/// Fetches whatever doctor would report missing, into the per-user cache.
/// </summary>
/// <remarks>
/// The checkout's PowerShell scripts stay the acquisition path for CI and for
/// working on GB# itself. This is the path for an installed <c>gbsharp</c>
/// tool, which has no checkout around it and cannot assume pwsh exists.
/// Everything fetched lands in <see cref="ToolchainCache"/>, which every
/// locator probes after the repo-relative candidates, so a checkout's
/// vendored copies still win inside one.
/// </remarks>
static bool Fix(string? gbdkPath)
{
    bool succeeded = true;

    if (!GbdkToolchain.TryLocate(gbdkPath, out _, out _))
    {
        if (ToolchainLock.Find("gbdk.lock.json") is not { } gbdkLock)
        {
            Console.Error.WriteLine(
                "error: no gbdk.lock.json was found in an enclosing GB# checkout or beside " +
                "the gbsharp tool, so there is no pinned GBDK to fetch.");
            succeeded = false;
        }
        else if (!GbdkFetcher.TryEnsureInstalled(
                     gbdkLock, ToolchainCache.GbdkDirectory, Console.WriteLine, out string? gbdkError))
        {
            Console.Error.WriteLine($"error: {gbdkError}");
            succeeded = false;
        }
    }

    if (PlayerStub.Installed() is null || !EmulatorRuntime.IsAvailable)
    {
        if (ToolchainLock.Find("emulator.lock.json") is not { } emulatorLock)
        {
            Console.Error.WriteLine(
                "error: no emulator.lock.json was found in an enclosing GB# checkout or beside " +
                "the gbsharp tool, so there is no pinned emulator runtime to fetch.");
            succeeded = false;
        }
        else if (!EmulatorFetcher.TryEnsureInstalled(
                     emulatorLock, ToolchainCache.EmulatorDirectory, Console.WriteLine, out string? emulatorError))
        {
            Console.Error.WriteLine($"error: {emulatorError}");
            succeeded = false;
        }
    }

    Console.WriteLine();
    return succeeded;
}

/// <summary>
/// The framework assembly sits next to the CLI, which references it, so game
/// code always compiles against the framework shipped with this compiler.
/// </summary>
static string FrameworkAssemblyPath() =>
    Path.Combine(AppContext.BaseDirectory, "GBSharp.Framework.dll");

/// <summary>
/// Finds the GBSharp.CLI project directory this process was run from, so a
/// scaffolded project's VS Code tasks can call back into the exact same CLI.
/// </summary>
/// <remarks>
/// When run via 'dotnet run --project &lt;path&gt;', which is how anyone working on GB#
/// itself runs it, AppContext.BaseDirectory is
/// &lt;project&gt;/bin/&lt;Config&gt;/&lt;TFM&gt;/. Walking up three levels and checking for
/// the .csproj confirms that guess instead of trusting it blindly. Returns
/// null when it does not hold, which is what running as the installed
/// 'gbsharp' tool looks like; the templates then write the bare command
/// instead of a path into a checkout that does not exist.
/// </remarks>
static string? ResolveCliProjectDirectory()
{
    DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent;
    if (directory is null)
    {
        return null;
    }

    string csproj = Path.Combine(directory.FullName, "GBSharp.CLI.csproj");
    return File.Exists(csproj) ? directory.FullName : null;
}
