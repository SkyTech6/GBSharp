using System.Reflection;
using System.Runtime.InteropServices;

namespace GBSharp.Emulator;

/// <summary>
/// Which build of the runtime to load.
/// </summary>
/// <remarks>
/// Upstream binjgb compiles its emulator twice, once plain and once with the
/// instrumentation hooks enabled, because <c>emulator-debug.c</c> includes
/// <c>emulator.c</c>. Instrumentation is therefore a property of the file on
/// disk rather than a flag, and the two ship side by side exporting the same
/// ABI. One process loads one of them.
/// </remarks>
public enum EmulatorFlavour
{
    /// <summary>No instrumentation. What a published game runs on.</summary>
    Fast,

    /// <summary>Breakpoints, disassembly, tracing and counters compiled in.</summary>
    Debug,
}

/// <summary>
/// Finds and loads the native runtime fetched by <c>tools/get-emulator.ps1</c>.
/// </summary>
/// <remarks>
/// Locates the install the way <c>GbdkToolchain</c> locates GBDK, for the same
/// reason: there is one acquisition story, and failing to find the thing has to
/// produce an actionable message rather than a <see cref="DllNotFoundException"/>
/// from somewhere inside a test.
/// </remarks>
public static class EmulatorRuntime
{
    /// <summary>
    /// The ABI this assembly was written against, checked against
    /// <c>gbsharp_abi_version()</c> at load.
    /// </summary>
    /// <remarks>
    /// The native library is fetched by a script and this assembly is built
    /// from source, so the two versions move independently and "close enough"
    /// has to be a refusal with a message rather than a crash three calls in.
    /// </remarks>
    public const uint AbiVersion = 4;

    /// <summary>Points at an install, skipping the search. Mirrors <c>GBDK_HOME</c>.</summary>
    public const string HomeVariable = "GBSHARP_EMULATOR_HOME";

    /// <summary>Selects a flavour without a code change. <c>fast</c> or <c>debug</c>.</summary>
    public const string FlavourVariable = "GBSHARP_EMULATOR_FLAVOUR";

    private static readonly object Gate = new();
    private static EmulatorFlavour s_requested = FlavourFromEnvironment();
    private static EmulatorFlavour? s_loaded;
    private static string? s_loadedPath;

    /// <summary>
    /// Registers the resolver. Called from <see cref="NativeMethods"/>'s type
    /// initializer, which the runtime runs before it binds the first P/Invoke
    /// in that class, so the resolver is always in place in time no matter
    /// which entry point is reached first.
    /// </summary>
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);

    /// <summary>The flavour that was loaded, once anything has been loaded.</summary>
    public static EmulatorFlavour? LoadedFlavour
    {
        get { lock (Gate) { return s_loaded; } }
    }

    /// <summary>The file that was loaded, for diagnostics.</summary>
    public static string? LoadedPath
    {
        get { lock (Gate) { return s_loadedPath; } }
    }

    /// <summary>
    /// Loads the requested flavour and verifies its ABI version.
    /// </summary>
    /// <remarks>
    /// Idempotent for the same flavour. Asking for a second, different flavour
    /// throws: a process holds one native library, and silently handing back the
    /// other one would make <see cref="GameBoy"/> instances disagree about
    /// whether instrumentation exists.
    /// </remarks>
    public static void Load(EmulatorFlavour flavour)
    {
        lock (Gate)
        {
            if (s_loaded is { } loaded)
            {
                if (loaded != flavour)
                {
                    throw new InvalidOperationException(
                        $"The {loaded} flavour of the emulator runtime is already loaded from " +
                        $"{s_loadedPath}, so the {flavour} flavour cannot be loaded as well. " +
                        "A process gets one flavour; choose it before the first emulator is created.");
                }

                return;
            }

            s_requested = flavour;
        }

        // Outside the lock: this triggers Resolve, which takes it.
        uint native = NativeMethods.AbiVersion();
        if (native != AbiVersion)
        {
            throw new InvalidOperationException(
                $"The emulator runtime at {LoadedPath} reports ABI version {native}, but this " +
                $"build of GBSharp.Emulator speaks version {AbiVersion}. " +
                "Run 'gbsharp doctor --fix' (or tools/get-emulator.ps1 in a checkout) to fetch the pinned runtime." +
                (Version is { } version ? $" The install found is {version}." : string.Empty));
        }

        if (flavour == EmulatorFlavour.Debug && !NativeMethods.HasDebugSupport())
        {
            throw new InvalidOperationException(
                $"The emulator runtime at {LoadedPath} was asked for the debug flavour but " +
                "reports no debug support, so the wrong file was loaded.");
        }
    }

    /// <summary>Loads the default flavour if nothing has been loaded yet.</summary>
    internal static void EnsureLoaded()
    {
        if (LoadedFlavour is null)
        {
            Load(s_requested);
        }
    }

    /// <summary>The pinned version the fetch script recorded, when there is one.</summary>
    public static string? Version
    {
        get
        {
            if (!TryLocate(null, out string? root, out _) || root is null)
            {
                return null;
            }

            string stamp = Path.Combine(root, ".gbsharp-version");
            return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
        }
    }

    /// <summary>
    /// True when a runtime is present to load, without loading it.
    /// </summary>
    /// <remarks>
    /// The integration tests use this to skip themselves on a bare checkout, so
    /// it must answer without throwing and without leaving a native library
    /// mapped into a process that was only asking a question.
    /// </remarks>
    public static bool IsAvailable => TryLocate(null, out _, out _);

    /// <summary>
    /// Finds an install, reporting every location searched so that a failure is
    /// diagnosable rather than mysterious.
    /// </summary>
    public static bool TryLocate(string? explicitPath, out string? root, out IReadOnlyList<string> searched)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home)
        {
            candidates.Add(home);
        }

        // The repository's own copy, fetched by tools/get-emulator.ps1.
        foreach (string ancestor in EnumerateAncestors(AppContext.BaseDirectory))
        {
            candidates.Add(Path.Combine(ancestor, "tools", "emulator"));
        }

        foreach (string ancestor in EnumerateAncestors(Directory.GetCurrentDirectory()))
        {
            candidates.Add(Path.Combine(ancestor, "tools", "emulator"));
        }

        // The per-user cache 'gbsharp doctor --fix' installs into, which is how
        // the packaged tool gets a runtime with no checkout anywhere near it.
        // Mirrors ToolchainCache in GBSharp.Backend.GBDK; spelled out here
        // because this assembly deliberately references nothing.
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gbsharp",
            "emulator"));

        var seen = new List<string>();
        foreach (string candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            seen.Add(full);

            // A root counts as an install only if the fast flavour is in it. The
            // debug flavour is checked when it is asked for, so that an archive
            // carrying one and not the other fails where it is used.
            if (File.Exists(LibraryPath(full, EmulatorFlavour.Fast)))
            {
                root = full;
                searched = seen;
                return true;
            }
        }

        root = null;
        searched = seen;
        return false;
    }

    /// <summary>
    /// The platform's file name for a flavour: <c>gbsharp_emulator.dll</c>,
    /// <c>libgbsharp_emulator.so</c> or <c>libgbsharp_emulator.dylib</c>.
    /// </summary>
    public static string LibraryFileName(EmulatorFlavour flavour)
    {
        string stem = flavour == EmulatorFlavour.Debug
            ? "gbsharp_emulator_debug"
            : "gbsharp_emulator";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return stem + ".dll";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "lib" + stem + ".dylib"
            : "lib" + stem + ".so";
    }

    private static string LibraryPath(string root, EmulatorFlavour flavour) =>
        Path.Combine(root, "bin", LibraryFileName(flavour));

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.Library, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        lock (Gate)
        {
            EmulatorFlavour flavour = s_loaded ?? s_requested;

            if (!TryLocate(null, out string? root, out IReadOnlyList<string> searched) || root is null)
            {
                throw new InvalidOperationException(
                    "The GB# emulator runtime was not found. Run 'gbsharp doctor --fix' " +
                    "(or tools/get-emulator.ps1 in a checkout) to fetch it. " +
                    "Looked in: " + string.Join(", ", searched.Take(4)));
            }

            string path = LibraryPath(root, flavour);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"The {flavour} flavour of the emulator runtime is missing from {root}: " +
                    $"expected {path}. Re-run tools/get-emulator.ps1 -Force.");
            }

            nint library = NativeLibrary.Load(path);
            s_loaded = flavour;
            s_loadedPath = path;
            return library;
        }
    }

    private static EmulatorFlavour FlavourFromEnvironment() =>
        Environment.GetEnvironmentVariable(FlavourVariable) is { Length: > 0 } value &&
        value.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? EmulatorFlavour.Debug
            : EmulatorFlavour.Fast;

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }
}
