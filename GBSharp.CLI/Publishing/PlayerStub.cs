using GBSharp.Backend.GBDK.Toolchain;

namespace GBSharp.Cli.Publishing;

/// <summary>
/// Finds the prebuilt Player for a target platform.
/// </summary>
/// <remarks>
/// <para>
/// Publishing cannot link, because the person publishing has no C toolchain. It
/// copies a prebuilt stub instead, and that stub has to be the one for the
/// platform being published for, which is usually but not always the platform
/// doing the publishing.
/// </para>
/// <para>
/// So this reads the same <c>tools/emulator.lock.json</c> that
/// <c>tools/get-emulator.ps1</c> reads, and fetches whichever platform's archive
/// is asked for, verifying the same SHA256 before unpacking it. One lock file
/// pins every platform, which is what makes publishing for a machine you do not
/// own as trustworthy as building for the one you are sitting at.
/// </para>
/// </remarks>
public static class PlayerStub
{
    /// <summary>Runtime identifiers a game can be published for.</summary>
    /// <remarks>
    /// "web" is in the list because it is a thing to publish for, not because it
    /// is a runtime identifier: it names a browser rather than a machine, and it
    /// resolves to a wasm module rather than to an executable stub.
    /// </remarks>
    public static readonly string[] SupportedRids =
        ["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", WebPublisher.Rid];

    /// <summary>The RID of the machine running this command.</summary>
    public static string HostRid
    {
        get
        {
            string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

            if (OperatingSystem.IsWindows())
            {
                return "win-x64";
            }

            return OperatingSystem.IsMacOS() ? $"osx-{architecture}" : $"linux-{architecture}";
        }
    }

    /// <summary>The Player's file name on a platform.</summary>
    public static string PlayerFileName(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "gbsharp-player.exe" : "gbsharp-player";

    /// <summary>
    /// The web runtime directory, fetching the archive if it is not already
    /// unpacked.
    /// </summary>
    /// <remarks>
    /// One archive rather than one per platform, because wasm is the same bytes
    /// everywhere, which is most of the point of it.
    /// </remarks>
    public static string? ResolveWebRuntime(Action<string> report, out string? error)
    {
        error = null;

        foreach (string installRoot in InstallRoots())
        {
            if (WebRuntimeIn(Path.Combine(installRoot, "stubs", WebPublisher.Rid)) is { } present)
            {
                return present;
            }
        }

        if (!TryReadAsset(WebPublisher.Rid, out string? url, out string? sha256, out error))
        {
            return null;
        }

        string root = Path.Combine(InstallRoots().First(), "stubs", WebPublisher.Rid);
        if (!ToolchainFetcher.TryFetch(url!, sha256!, root, report, out error))
        {
            return null;
        }

        if (WebRuntimeIn(root) is not { } fetched)
        {
            error = "The web runtime archive does not contain gbsharp.wasm. It may have been " +
                    "published before the web runtime existed.";
            return null;
        }

        return fetched;
    }

    /// <summary>
    /// The directory holding the wasm, whether the archive wrapped it in a
    /// <c>web</c> directory or not.
    /// </summary>
    /// <remarks>
    /// Unpacking collapses a single top-level directory, so which of the two
    /// layouts arrives depends on how many entries the archive had at its root.
    /// Tolerating both is cheaper than pinning it and finding out from a
    /// published release that it moved.
    /// </remarks>
    private static string? WebRuntimeIn(string root)
    {
        foreach (string candidate in new[] { root, Path.Combine(root, "web") })
        {
            if (File.Exists(Path.Combine(candidate, "gbsharp.wasm")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The Player already unpacked for this machine, or null.
    /// </summary>
    /// <remarks>
    /// Never downloads. <c>gbsharp run</c> uses this, and a build command that
    /// silently reached for the network before it could show you your game would
    /// be a worse default than not running it at all.
    /// </remarks>
    public static string? Installed()
    {
        foreach (string installRoot in InstallRoots())
        {
            string path = Path.Combine(installRoot, "bin", PlayerFileName(HostRid));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The stub for <paramref name="rid"/>, fetching it if it is not already
    /// unpacked.
    /// </summary>
    /// <param name="report">Progress, so a download does not look like a hang.</param>
    /// <returns>The path to the Player executable, or null with a reason.</returns>
    public static string? Resolve(string rid, Action<string> report, out string? error)
    {
        error = null;

        if (!SupportedRids.Contains(rid))
        {
            error = $"'{rid}' is not a platform GB# publishes for. Use one of: " +
                    string.Join(", ", SupportedRids) + ".";
            return null;
        }

        // The host's own runtime is already unpacked by tools/get-emulator.ps1
        // or 'gbsharp doctor --fix', so publishing for the machine you are on
        // needs no download at all.
        if (rid == HostRid && Installed() is { } installed)
        {
            return installed;
        }

        foreach (string installRoot in InstallRoots())
        {
            string present = Path.Combine(installRoot, "stubs", rid, "bin", PlayerFileName(rid));
            if (File.Exists(present))
            {
                return present;
            }
        }

        if (!TryReadAsset(rid, out string? url, out string? sha256, out error))
        {
            return null;
        }

        string destination = Path.Combine(InstallRoots().First(), "stubs", rid);
        if (!ToolchainFetcher.TryFetch(url!, sha256!, destination, report, out error))
        {
            return null;
        }

        string cached = Path.Combine(destination, "bin", PlayerFileName(rid));
        if (!File.Exists(cached))
        {
            error = $"The runtime archive for {rid} does not contain {PlayerFileName(rid)}. " +
                    "It may have been published before the Player existed.";
            return null;
        }

        return cached;
    }

    /// <summary>
    /// Files that have to sit beside a published game for it to start.
    /// </summary>
    /// <remarks>
    /// On Windows the archive carries SDL2.dll, because the Player links it
    /// dynamically and Windows has no system copy. A published game is therefore
    /// a small folder rather than a single file. Making it one file needs a
    /// statically linked SDL in the runtime's release build, which is a change to
    /// the emulator repository rather than to this command.
    /// </remarks>
    public static IEnumerable<string> RuntimeCompanions(string stubPath)
    {
        string? directory = Path.GetDirectoryName(stubPath);
        if (directory is null)
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(file);

            // The emulator libraries are linked into the Player statically, so
            // only a windowing library ever needs to travel with a game.
            if (name.StartsWith("SDL", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// The pinned archive for a rid, from the checkout's lock file or the copy
    /// packed beside the gbsharp tool.
    /// </summary>
    private static bool TryReadAsset(string rid, out string? url, out string? sha256, out string? error)
    {
        url = null;
        sha256 = null;

        string? lockPath = ToolchainLock.Find("emulator.lock.json");
        if (lockPath is null)
        {
            error = "No emulator.lock.json was found in an enclosing GB# checkout or beside " +
                    "the gbsharp tool, so there is no pinned runtime to publish with.";
            return false;
        }

        if (!ToolchainLock.TryRead(lockPath, out ToolchainLock? lockFile, out error) || lockFile is null)
        {
            return false;
        }

        if (!lockFile.TryGetAsset(rid, out string pinnedUrl, out string pinnedSha256))
        {
            error = $"{lockPath} pins no runtime for {rid}.";
            return false;
        }

        url = pinnedUrl;
        sha256 = pinnedSha256;
        return true;
    }

    /// <summary>
    /// Every directory an emulator runtime may be installed in, most specific
    /// first: a checkout's <c>tools/emulator</c> when running inside one, then
    /// the per-user cache <c>gbsharp doctor --fix</c> fills. The first entry
    /// is also where a fetch lands, so a checkout keeps its runtime vendored
    /// and a packaged tool keeps it in the cache.
    /// </summary>
    private static IEnumerable<string> InstallRoots()
    {
        if (FindToolsDirectory() is { } tools)
        {
            yield return Path.Combine(tools, "emulator");
        }

        yield return ToolchainCache.EmulatorDirectory;
    }

    /// <summary>Walks up for the tools directory, the way the runtime loader does.</summary>
    private static string? FindToolsDirectory()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "tools");
                if (File.Exists(Path.Combine(candidate, "emulator.lock.json")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
