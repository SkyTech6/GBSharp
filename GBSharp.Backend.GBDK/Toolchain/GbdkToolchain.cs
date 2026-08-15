using System.Runtime.InteropServices;

namespace GBSharp.Backend.GBDK.Toolchain;

/// <summary>
/// Locates a GBDK-2020 installation.
/// </summary>
/// <remarks>
/// GB# stands on GBDK rather than replacing it (thesis section 25), so finding
/// it is a first-class concern and failing to find it has to produce an
/// actionable message rather than a missing-file exception.
/// </remarks>
public sealed class GbdkToolchain
{
    private GbdkToolchain(string root, string compilerDriver)
    {
        Root = root;
        CompilerDriver = compilerDriver;
    }

    /// <summary>The GBDK install root.</summary>
    public string Root { get; }

    /// <summary>Absolute path to <c>lcc</c>, the GBDK compiler driver.</summary>
    public string CompilerDriver { get; }

    /// <summary>
    /// Absolute path to GBDK's <c>romusage</c>, which reads a linker map and
    /// reports per-bank usage. May not exist on a hand-assembled install, so
    /// callers check rather than assume.
    /// </summary>
    public string RomUsage => Path.Combine(Root, "bin", RomUsageFileName);

    /// <summary>The pinned version, when the bootstrap script recorded one.</summary>
    public string? Version =>
        File.Exists(Path.Combine(Root, ".gbsharp-version"))
            ? File.ReadAllText(Path.Combine(Root, ".gbsharp-version")).Trim()
            : null;

    /// <summary>
    /// Finds GBDK, reporting every location searched so the failure is
    /// diagnosable rather than mysterious.
    /// </summary>
    public static bool TryLocate(string? explicitPath, out GbdkToolchain? toolchain, out IReadOnlyList<string> searched)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        if (Environment.GetEnvironmentVariable("GBDK_HOME") is { Length: > 0 } home)
        {
            candidates.Add(home);
        }

        // The repository's own vendored copy, fetched by tools/get-gbdk.ps1.
        foreach (string root in EnumerateAncestors(AppContext.BaseDirectory))
        {
            candidates.Add(Path.Combine(root, "tools", "gbdk"));
        }

        foreach (string root in EnumerateAncestors(Directory.GetCurrentDirectory()))
        {
            candidates.Add(Path.Combine(root, "tools", "gbdk"));
        }

        // The per-user cache 'gbsharp doctor --fix' installs into, which is how
        // the packaged tool gets a toolchain with no checkout anywhere near it.
        // Probed last so a checkout's vendored copy still wins inside one.
        candidates.Add(ToolchainCache.GbdkDirectory);

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

            string driver = Path.Combine(full, "bin", DriverFileName);
            if (File.Exists(driver))
            {
                toolchain = new GbdkToolchain(full, driver);
                searched = seen;
                return true;
            }
        }

        toolchain = null;
        searched = seen;
        return false;
    }

    private static string DriverFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "lcc.exe" : "lcc";

    private static string RomUsageFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "romusage.exe" : "romusage";

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }
}
