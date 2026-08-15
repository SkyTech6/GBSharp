using System.Diagnostics;
using System.Text.Json;
using GBSharp.Backend.GBDK.Reporting;
using GBSharp.Backend.GBDK.Toolchain;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.IR;

namespace GBSharp.Backend.GBDK;

/// <summary>The machine a ROM is built for.</summary>
public enum GBTarget
{
    /// <summary>Original Game Boy (DMG).</summary>
    GameBoy,

    /// <summary>Game Boy Color.</summary>
    GameBoyColor,
}

/// <summary>
/// The cartridge type byte, 0x147.
/// </summary>
/// <remarks>
/// The values are the header bytes themselves rather than an arbitrary
/// enumeration, so the linker flag is the value and there is no table to keep
/// in step with the hardware.
/// </remarks>
public enum MbcType
{
    /// <summary>No mapper: 32 KB, the whole cartridge always mapped.</summary>
    None = 0x00,

    Mbc1 = 0x01,

    Mbc5 = 0x19,

    Mbc5Ram = 0x1A,

    /// <summary>MBC5 with battery-backed save RAM. GBDK's own recommended default.</summary>
    Mbc5RamBattery = 0x1B,
}

/// <summary>
/// What a cartridge type byte implies, for the mappers GB# will declare.
/// </summary>
public static class MbcTypes
{
    /// <summary>
    /// True if the mapper says save RAM is on the cartridge, and so needs a
    /// non-zero RAM size at 0x149 for the header to be self-consistent.
    /// </summary>
    public static bool DeclaresRam(this MbcType mbc) =>
        mbc is MbcType.Mbc5Ram or MbcType.Mbc5RamBattery;
}

/// <param name="OutputDirectory">Where the ROM and generated C are written.</param>
/// <param name="KeepGeneratedC">
/// Retain the generated C after a successful build. Always retained on failure,
/// because a backend failure is undiagnosable without it.
/// </param>
/// <param name="GbdkPath">An explicit GBDK root, overriding discovery.</param>
public sealed record RomBuildOptions(
    string OutputDirectory,
    GBTarget Target = GBTarget.GameBoy,
    bool KeepGeneratedC = false,
    string? GbdkPath = null)
{
    /// <summary>
    /// The mapper to declare. Ignored entirely when nothing is banked.
    /// </summary>
    public MbcType Cartridge { get; init; } = MbcType.None;

    /// <summary>
    /// How many 16 KB ROM banks to reserve, or null to let the linker decide.
    /// </summary>
    public int? RomBankCount { get; init; }

    /// <summary>
    /// How many 8 KB save RAM banks to reserve, or null to let the mapper
    /// decide: a mapper that names RAM gets one bank, anything else gets none.
    /// </summary>
    /// <remarks>
    /// Nullable so that "reserve nothing" can be asked for explicitly and
    /// distinguished from saying nothing at all. Without the distinction the
    /// default cartridge (MBC5 with battery-backed RAM) reserves no RAM, and
    /// the header advertises a battery with nothing behind it.
    /// </remarks>
    public int? RamBankCount { get; init; }

    /// <summary>
    /// External object/library files to link into the ROM, such as a prebuilt
    /// hUGEDriver. Copied beside the generated C and handed to lcc as bare
    /// positional arguments, the same way the runtime shim and every
    /// translation unit already are. Empty by default, so a project that
    /// declares none invokes lcc exactly as it did before this existed.
    /// </summary>
    public IReadOnlyList<string> Libraries { get; init; } = [];

    /// <summary>
    /// C header files to include in the generated C, so [Native] declarations
    /// can call functions the framework does not wrap, typically the
    /// prototypes for a companion .c file under <see cref="Libraries"/>.
    /// Copied beside the generated C and included after the runtime header.
    /// Empty by default, so a project that declares none emits exactly the C
    /// it did before this existed.
    /// </summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>
    /// Comment every generated statement with the C# line that produced it, and
    /// write <c>sourcemap.json</c> beside the generated C.
    /// </summary>
    public bool AnnotateSource { get; init; }
}

/// <param name="RomPath">The built ROM, or null if the build failed.</param>
/// <param name="GeneratedCDirectory">Where the generated C is, when it was kept.</param>
/// <param name="GeneratedFiles">The files that were emitted, in write order.</param>
/// <param name="Usage">What the linker placed, when it could be read.</param>
public sealed record RomBuildResult(
    string? RomPath,
    string? GeneratedCDirectory,
    IReadOnlyList<EmittedFile> GeneratedFiles,
    IReadOnlyList<GBDiagnostic> Diagnostics,
    RomUsageReport? Usage = null)
{
    public bool Succeeded => RomPath is not null;
}

/// <summary>
/// Turns GB# IR into a ROM by emitting C and driving GBDK.
/// </summary>
public sealed class RomBuilder
{
    /// <summary>
    /// The runtime shim files copied next to the generated C.
    /// </summary>
    /// <remarks>
    /// The header is almost all of it: bare <c>inline</c> wrappers that vanish
    /// into their callers. The <c>.c</c> holds only what cannot be inlined,
    /// which today is the handful of functions that switch ROM banks.
    /// </remarks>
    private static readonly string[] RuntimeFiles = ["gbs_runtime.h", "gbs_runtime.c"];

    private const string RuntimeSource = "gbs_runtime.c";


    public RomBuildResult Build(IRModule module, RomBuildOptions options)
    {
        var diagnostics = new DiagnosticBag();

        if (!GbdkToolchain.TryLocate(options.GbdkPath, out GbdkToolchain? toolchain, out IReadOnlyList<string> searched) ||
            toolchain is null)
        {
            diagnostics.Report(GBDiagnostics.GbdkNotFound, SourceSpan.None, string.Join(", ", searched.Take(4)));
            return new RomBuildResult(null, null, [], diagnostics.Diagnostics);
        }

        var emitter = new CEmitter(options.AnnotateSource, options.Includes.Select(Path.GetFileName).ToList()!);
        IReadOnlyList<EmittedFile> files = emitter.Emit(module);

        // The generated C gets its own directory so the ROM and the linker
        // output stay visible beside it rather than buried among N .c files.
        string sourceDirectory = Path.Combine(options.OutputDirectory, "c");
        Directory.CreateDirectory(sourceDirectory);

        foreach (EmittedFile file in files)
        {
            File.WriteAllText(Path.Combine(sourceDirectory, file.Name), file.Text);
        }

        if (options.AnnotateSource)
        {
            // Beside the generated C rather than the ROM: it describes that C,
            // and it follows the same lifetime: kept with --emit-c, cleaned up
            // with it otherwise.
            string sourceMapPath = Path.Combine(sourceDirectory, "sourcemap.json");
            File.WriteAllText(
                sourceMapPath,
                JsonSerializer.Serialize([.. emitter.SourceMap], SourceMapJson.Default.SourceMapEntryArray));
        }

        CopyRuntimeFiles(sourceDirectory);
        CopyLibraries(options.Libraries, sourceDirectory);
        CopyLibraries(options.Includes, sourceDirectory);

        string extension = options.Target == GBTarget.GameBoyColor ? ".gbc" : ".gb";
        string romPath = Path.Combine(options.OutputDirectory, module.Name + extension);

        int exitCode = InvokeCompiler(toolchain, module, options, sourceDirectory, files, romPath, out string toolOutput);

        if (exitCode != 0 || !File.Exists(romPath))
        {
            // The generated C is always kept on failure: it is the only way to
            // tell a GB# codegen bug from an SDCC one.
            diagnostics.Report(GBDiagnostics.BackendCompileFailed, SourceSpan.None, exitCode, sourceDirectory);

            if (toolOutput.Length > 0)
            {
                diagnostics.Report(GBDiagnostics.InternalError, SourceSpan.None, toolOutput.Trim());
            }

            return new RomBuildResult(null, sourceDirectory, files, diagnostics.Diagnostics);
        }

        // The map is what makes the resource report true rather than estimated.
        // It lands beside the ROM rather than beside the generated C, because
        // the linker names its products after the output file, and it stays
        // there: a .sym is what an emulator's debugger loads, and deleting it
        // with the generated C would make the ROM harder to debug, not tidier.
        string mapPath = Path.Combine(options.OutputDirectory, Path.GetFileNameWithoutExtension(romPath) + ".map");

        // Before the percentages, because an overflowed bank 0 makes them
        // misleading: the spilled bytes are counted against the bank whose
        // addresses they landed in, so both banks look merely busy.
        ReportResidentOverflow(mapPath, diagnostics);

        if (!RomUsageReader.TryRead(toolchain, mapPath, out RomUsageReport? usage, out string? usageFailure))
        {
            diagnostics.Report(GBDiagnostics.BankUsageUnavailable, SourceSpan.None, usageFailure ?? "unknown reason");
        }
        else if (usage is not null)
        {
            ReportResidentPressure(usage, diagnostics);
        }

        string symbolPath = Path.Combine(
            options.OutputDirectory, Path.GetFileNameWithoutExtension(romPath) + ".sym");

        // Beside the .sym, and for the same reason it is: the two are read
        // together, by anything turning a running address back into a method,
        // and separating them would leave half a chain on disk. Unlike the
        // source map this is not gated on --annotate-source, because it maps
        // whole functions rather than statements and costs one line each.
        File.WriteAllText(
            Path.Combine(
                options.OutputDirectory,
                Path.GetFileNameWithoutExtension(romPath) + ".functions.json"),
            JsonSerializer.Serialize(
                FunctionMapEntry.From(module), FunctionMapJson.Default.FunctionMapEntryArray));

        ReportAutomaticPlacements(module, symbolPath, diagnostics);

        ReportBudgets(module, romPath, usage, diagnostics);

        string? keptSource = sourceDirectory;
        if (!options.KeepGeneratedC)
        {
            TryDeleteDirectory(sourceDirectory);
            keptSource = null;
        }

        return new RomBuildResult(romPath, keptSource, files, diagnostics.Diagnostics, usage);
    }

    private static int InvokeCompiler(
        GbdkToolchain toolchain,
        IRModule module,
        RomBuildOptions options,
        string sourceDirectory,
        IReadOnlyList<EmittedFile> files,
        string romPath,
        out string output)
    {
        var arguments = new List<string>();

        if (options.Target == GBTarget.GameBoyColor)
        {
            // Mark the ROM as Game Boy Color compatible.
            arguments.Add("-Wm-yC");
        }

        // The 11-character ROM title in the cartridge header.
        string title = module.Name.ToUpperInvariant();
        arguments.Add($"-Wm-yn{(title.Length > 11 ? title[..11] : title)}");

        // Ask the linker for the artefacts that make the build report true and
        // the ROM debuggable: a map, a .noi, and the .sym an emulator loads.
        // None of these are produced by default.
        arguments.Add("-Wl-m");
        arguments.Add("-Wl-j");
        arguments.Add("-Wm-yS");

        AddBankingArguments(arguments, options, files);

        // Keep lcc's intermediates in the build directory rather than letting
        // concurrent builds race over the same names in the system temp folder.
        arguments.Add("-tempdir=.");

        arguments.Add("-o");

        // The ROM sits one level up from the generated C, and lcc runs with its
        // working directory set to the source directory. Every path handed to
        // lcc stays relative, which is what keeps a project directory containing
        // a space from ever reaching its command line.
        arguments.Add("../" + Path.GetFileName(romPath));

        foreach (EmittedFile file in files.Where(f => f.Kind == EmittedFileKind.TranslationUnit))
        {
            arguments.Add(file.Name);
        }

        // The shim's own translation unit, holding the bank-switching functions
        // that cannot be inlined. Bare relative name, like everything else here.
        arguments.Add(RuntimeSource);

        // External libraries the project declared (a prebuilt hUGEDriver, say).
        // Copied beside the generated C above, so only the bare file name - not
        // the project-directory path it actually lives at - reaches lcc. Passed
        // as positional arguments like any other object file, not with -l: that
        // flag asks GBDK to resolve a library *name* on its own search path,
        // which is not what a path the developer handed us needs. Empty by
        // default, so a project with none adds nothing here.
        foreach (string library in options.Libraries)
        {
            arguments.Add(Path.GetFileName(library));
        }

        var startInfo = new ProcessStartInfo(toolchain.CompilerDriver)
        {
            WorkingDirectory = sourceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            output = "Could not start the GBDK compiler driver.";
            return -1;
        }

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        output = (standardOutput + standardError).Trim();
        return process.ExitCode;
    }

    /// <summary>
    /// Fails the build when a declared budget was exceeded.
    /// </summary>
    /// <remarks>
    /// Checked here, after the link, because none of these questions can be
    /// answered earlier. Work RAM is read from the map so it counts the stack,
    /// shadow OAM and GBDK's own state; ROM size and bank count come from the
    /// cartridge header, so they describe the image that exists rather than the
    /// one that was requested.
    /// </remarks>
    private static void ReportBudgets(
        IRModule module,
        string romPath,
        RomUsageReport? usage,
        DiagnosticBag diagnostics)
    {
        if (!module.Budgets.Any)
        {
            return;
        }

        if (module.Budgets.MaxWram is { } maxWram && usage is not null && usage.WramUsed > maxWram)
        {
            diagnostics.Report(GBDiagnostics.WramBudgetExceeded, SourceSpan.None, usage.WramUsed, maxWram);
        }

        RomHeader? header = RomHeader.Read(romPath);

        if (module.Budgets.MaxRom is { } maxRom && header is not null && header.SizeInBytes > maxRom)
        {
            diagnostics.Report(GBDiagnostics.RomBudgetExceeded, SourceSpan.None, header.SizeInBytes, maxRom);
        }

        if (module.Budgets.MaxRomBanks is { } maxBanks && header is not null && header.DeclaredRomBanks > maxBanks)
        {
            diagnostics.Report(GBDiagnostics.BankBudgetExceeded, SourceSpan.None, header.DeclaredRomBanks, maxBanks);
        }
    }

    /// <summary>
    /// Says where the linker put everything GB# left it to place.
    /// </summary>
    /// <remarks>
    /// One line per automatically placed declaration, naming the bank it landed
    /// in and the attribute that would pin it there. Deferring the choice is
    /// allowed; leaving the developer unable to discover or override it is not.
    /// </remarks>
    private static void ReportAutomaticPlacements(IRModule module, string symbolPath, DiagnosticBag diagnostics)
    {
        // The C# name where there is one, because the developer is being told to
        // go and write an attribute on that declaration, not on a mangled symbol.
        (string Symbol, string Display, SourceSpan Span)[] automatic =
        [
            .. module.Functions
                .Where(f => f.Bank.Kind == IRBankKind.Automatic)
                .Select(f => (f.Name, f.SourceName ?? f.Name, f.Span)),
            .. module.Globals
                .Where(g => g.Bank.Kind == IRBankKind.Automatic)
                .Select(g => (g.Name, g.Name, g.Span)),
        ];

        if (automatic.Length == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, int> banks = SymbolMapReader.TryRead(symbolPath);

        foreach ((string symbol, string display, SourceSpan span) in automatic)
        {
            if (banks.TryGetValue(symbol, out int bank))
            {
                diagnostics.Report(GBDiagnostics.AutomaticPlacement, span, display, bank);
            }
        }
    }

    /// <summary>
    /// Errors when the linker ran an area past the end of the resident bank.
    /// </summary>
    /// <remarks>
    /// The ROM has already been written by this point and is kept, the same way
    /// the generated C is kept on a codegen failure: the artefact is the
    /// evidence. What it is not is loadable, so this is an error rather than a
    /// note. See <see cref="ResidentOverflowReader"/> for why the map is read
    /// directly here and nowhere else.
    /// </remarks>
    private static void ReportResidentOverflow(string mapPath, DiagnosticBag diagnostics)
    {
        if (ResidentOverflowReader.Read(mapPath) is not { } overflow)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.ResidentBankOverflow,
            SourceSpan.None,
            overflow.Bytes,
            overflow.Crossing.Name,
            $"0x{overflow.Crossing.Start:X4}",
            $"0x{overflow.Crossing.End:X4}");
    }

    /// <summary>
    /// Warns when bank 0 is close to full.
    /// </summary>
    /// <remarks>
    /// Read from the map rather than from what GB# placed, because bank 0 also
    /// holds the interrupt vectors and GBDK's own runtime, so the declared total
    /// would understate it. Bank 0 filling is the failure that no larger
    /// cartridge fixes, so it is worth saying before it happens rather than
    /// letting the linker say it afterwards.
    /// </remarks>
    private static void ReportResidentPressure(RomUsageReport usage, DiagnosticBag diagnostics)
    {
        const int WarnAtPercent = 90;

        BankUsage? bankZero = usage.Rom.FirstOrDefault(b => b.BankNumber == 0);
        if (bankZero is null || bankZero.Size == 0 || bankZero.UsedPercent < WarnAtPercent)
        {
            return;
        }

        diagnostics.Report(
            GBDiagnostics.ResidentBankFull,
            SourceSpan.None,
            bankZero.UsedPercent,
            bankZero.Used,
            bankZero.Size);
    }

    /// <summary>
    /// Adds the mapper and bank arguments, and nothing at all when nothing is banked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unbanked program must invoke lcc exactly as it did before banking
    /// existed: same flags, same ROM, byte for byte. That is what makes every
    /// existing integration test a regression guard for this feature rather than
    /// something that had to be re-baselined.
    /// </para>
    /// <para>
    /// Every argument here is a number or a literal, so the invariant that no
    /// path but a bare file name reaches lcc's command line still holds.
    /// </para>
    /// </remarks>
    private static void AddBankingArguments(
        List<string> arguments,
        RomBuildOptions options,
        IReadOnlyList<EmittedFile> files)
    {
        if (files.All(f => f.RomBank is null))
        {
            return;
        }

        // A banked ROM needs a mapper to switch with. Defaulting rather than
        // failing keeps [Bank] from also requiring a project file, and MBC5 with
        // battery-backed RAM is what GBDK's own examples use.
        MbcType cartridge = options.Cartridge == MbcType.None ? MbcType.Mbc5RamBattery : options.Cartridge;
        arguments.Add($"-Wl-yt0x{(int)cartridge:X2}");

        // The cartridge type byte and the RAM size byte are written by two
        // separate flags, so nothing but this makes them agree. A mapper that
        // names RAM and reserves none produces a header that lies: an emulator
        // reads the battery, offers to save, and finds nothing to save into.
        int ramBanks = options.RamBankCount ?? (cartridge.DeclaresRam() ? 1 : 0);

        if (ramBanks > 0)
        {
            arguments.Add($"-Wm-ya{ramBanks}");
        }

        bool automatic = files.Any(f => f.RomBank == CEmitter.AutoBankSentinel);

        if (options.RomBankCount is { } banks)
        {
            arguments.Add($"-Wl-yo{banks}");
        }
        else if (!automatic)
        {
            // Let the linker size the image. With -autobank this is implied, so
            // passing it as well would mix an explicit count with an automatic one.
            arguments.Add("-Wm-yoA");
        }

        if (automatic)
        {
            // bankpack rewrites object files, so it needs to know their extension.
            arguments.Add("-autobank");
            arguments.Add("-Wb-ext=.rel");

            // Makes bankpack print what it decided, which is where the automatic
            // placements are read back from.
            arguments.Add("-Wb-v");
        }
    }

    /// <summary>
    /// Places the runtime shim beside the generated C so the developer can read
    /// both together, and so the include resolves without an extra search path.
    /// </summary>
    private static void CopyRuntimeFiles(string outputDirectory)
    {
        foreach (string name in RuntimeFiles)
        {
            string destination = Path.Combine(outputDirectory, name);
            string? source = RuntimeCandidates(name).FirstOrDefault(File.Exists);

            if (source is null)
            {
                throw new FileNotFoundException(
                    $"The GB# runtime shim '{name}' was not found next to the backend assembly. " +
                    "The build output is incomplete.");
            }

            File.Copy(source, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Copies external libraries beside the generated C, the same way
    /// <see cref="CopyRuntimeFiles"/> copies the runtime shim, so that only a
    /// bare file name - never the project directory path the library actually
    /// lives at - ever reaches lcc's command line.
    /// </summary>
    private static void CopyLibraries(IReadOnlyList<string> libraries, string outputDirectory)
    {
        foreach (string library in libraries)
        {
            File.Copy(library, Path.Combine(outputDirectory, Path.GetFileName(library)), overwrite: true);
        }
    }

    private static IEnumerable<string> RuntimeCandidates(string name)
    {
        string baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, "Runtime", name);
        yield return Path.Combine(baseDirectory, name);

        // Running from the repository without a copy step.
        for (DirectoryInfo? directory = new(baseDirectory); directory is not null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "GBSharp.Backend.GBDK", "Runtime", name);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leaving a stale intermediate behind is not worth failing a build over.
        }
    }
}
