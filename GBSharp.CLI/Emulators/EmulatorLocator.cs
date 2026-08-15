namespace GBSharp.Cli.Emulators;

/// <summary>An emulator to launch, and how.</summary>
/// <param name="Executable">The program to start.</param>
/// <param name="Arguments">Already split and substituted.</param>
/// <param name="Describe">What to tell the developer was launched.</param>
/// <param name="LoadsSymbolsAutomatically">Whether it will find the .sym itself.</param>
public sealed record ResolvedEmulator(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Describe,
    bool LoadsSymbolsAutomatically);

/// <summary>
/// Finds an emulator to run a ROM in.
/// </summary>
/// <remarks>
/// <para>
/// In order: the project file, then an environment variable, then a per-user
/// settings file, then PATH, then the usual install directories. The project file
/// comes first because it is the most specific statement, and PATH comes before
/// the well-known directories because a developer who installed a build
/// deliberately usually put it there.
/// </para>
/// <para>
/// A machine-specific absolute path belongs in the per-user file rather than the
/// project, for the same reason <c>AssetBindings</c> refuses absolute asset paths:
/// a project that only runs on the machine that wrote it is not shareable, and the
/// failure lands on whoever cloned it.
/// </para>
/// </remarks>
public static class EmulatorLocator
{
    public const string EnvironmentVariable = "GBSHARP_EMULATOR";

    /// <summary>What to write in a project file to ask for the bundled Player.</summary>
    public const string BundledPlayerId = "player";

    /// <summary>
    /// The Player that ships with GB#, if it has been fetched.
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="EmulatorCatalog"/>, which describes emulators
    /// somebody else installed and this program has to go looking for. This one
    /// arrives with the toolchain at a known path, so searching for it would be
    /// pretending not to know where it is.
    /// </remarks>
    private static bool TryBundledPlayer(
        string romPath, List<string> probed, out ResolvedEmulator? emulator)
    {
        string? path = Publishing.PlayerStub.Installed();

        if (path is null)
        {
            probed.Add("the bundled GB# Player (run 'gbsharp doctor --fix' to fetch it)");
            emulator = null;
            return false;
        }

        emulator = new ResolvedEmulator(
            path,
            [romPath],
            "GB# Player",
            // It reads no .sym: it is what a player runs, not a debugger. That
            // is what --emulator bgb is for.
            LoadsSymbolsAutomatically: false);

        return true;
    }

    /// <summary>
    /// Resolves an emulator, or explains everywhere it looked.
    /// </summary>
    public static bool TryResolve(
        string? projectSetting,
        string? commandLine,
        string romPath,
        out ResolvedEmulator? emulator,
        out IReadOnlyList<string> searched)
    {
        var probed = new List<string>();

        foreach (string? candidate in new[]
                 {
                     commandLine,
                     projectSetting,
                     Environment.GetEnvironmentVariable(EnvironmentVariable),
                     UserSetting(),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (TryFromSetting(candidate!, romPath, probed, out emulator))
            {
                searched = probed;
                return true;
            }
        }

        // Nothing configured, so the bundled Player, which is always present
        // because the tests need it and needs no configuration because it is
        // shipped rather than found. It wins here on being reliably there, not
        // on being better: the emulators below have mature debuggers and read
        // the .sym RomBuilder writes, which is why they are still searched for
        // and still selectable by name.
        if (TryBundledPlayer(romPath, probed, out emulator))
        {
            searched = probed;
            return true;
        }

        foreach (KnownEmulator known in EmulatorCatalog.Known)
        {
            foreach (string name in known.ExecutableNames)
            {
                if (FindOnPath(name, probed) is { } found)
                {
                    emulator = Resolve(known, found, romPath);
                    searched = probed;
                    return true;
                }
            }
        }

        emulator = null;
        searched = probed;
        return false;
    }

    /// <summary>
    /// A setting is either a catalog id or a path to an executable.
    /// </summary>
    /// <remarks>
    /// Treating a bare id and a path the same way keeps the older
    /// <c>"emulator": "C:/tools/bgb.exe"</c> spelling working, which every
    /// existing project uses.
    /// </remarks>
    private static bool TryFromSetting(
        string setting,
        string romPath,
        List<string> probed,
        out ResolvedEmulator? emulator)
    {
        // Named explicitly, so that a project which wants the bundled player
        // even on a machine with BGB installed can say so.
        if (string.Equals(setting, BundledPlayerId, StringComparison.OrdinalIgnoreCase))
        {
            return TryBundledPlayer(romPath, probed, out emulator);
        }

        if (EmulatorCatalog.ById(setting) is { } known)
        {
            foreach (string name in known.ExecutableNames)
            {
                if (FindOnPath(name, probed) is { } found)
                {
                    emulator = Resolve(known, found, romPath);
                    return true;
                }
            }

            emulator = null;
            return false;
        }

        probed.Add(setting);

        if (File.Exists(setting))
        {
            // An unrecognised executable gets the plain treatment: the ROM path
            // and nothing else, which is what every Game Boy emulator accepts.
            KnownEmulator? match = EmulatorCatalog.Known.FirstOrDefault(e =>
                e.ExecutableNames.Any(n =>
                    string.Equals(Path.GetFileNameWithoutExtension(setting), n, StringComparison.OrdinalIgnoreCase)));

            emulator = match is null
                ? new ResolvedEmulator(setting, [romPath], Path.GetFileName(setting), false)
                : Resolve(match, setting, romPath);

            return true;
        }

        emulator = null;
        return false;
    }

    private static ResolvedEmulator Resolve(KnownEmulator known, string executable, string romPath) =>
        new(
            executable,
            EmulatorCatalog.BuildArguments(known.ArgumentTemplate, romPath),
            known.DisplayName,
            known.LoadsSymbolsAutomatically);

    /// <summary>The per-user emulator path, if one was written.</summary>
    /// <remarks>
    /// A single line of text rather than a settings format, because there is
    /// exactly one setting and inventing a schema for it would be worse.
    /// </remarks>
    private static string? UserSetting()
    {
        try
        {
            string path = Path.Combine(ConfigDirectory(), "emulator.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a per-user setting lives on this platform.</summary>
    public static string ConfigDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        string root = !string.IsNullOrWhiteSpace(xdg)
            ? xdg!
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Path.Combine(root, "gbsharp");
    }

    private static string? FindOnPath(string name, List<string> probed)
    {
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [string.Empty];

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;

                try
                {
                    candidate = Path.Combine(directory.Trim(), name + extension);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not this command's problem.
                    continue;
                }

                probed.Add(candidate);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
