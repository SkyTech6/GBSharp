using GBSharp.Backend.GBDK.Toolchain;
using GBSharp.Cli.Publishing;
using GBSharp.Emulator;

namespace GBSharp.Cli.Emulators;

/// <summary>
/// Fetches the pinned GB# emulator runtime, in process.
/// </summary>
/// <remarks>
/// The C# counterpart of <c>tools/get-emulator.ps1</c>, for the same reason
/// <see cref="GbdkFetcher"/> is the counterpart of <c>get-gbdk.ps1</c>: the
/// packaged <c>gbsharp</c> tool has no checkout and no pwsh to fetch with.
/// Same lock file, same stamp, same layout checks, so an install made by
/// either path is valid to the other.
/// </remarks>
public static class EmulatorFetcher
{
    /// <summary>
    /// Makes sure the pinned emulator runtime is installed at
    /// <paramref name="destination"/>, fetching it if missing or stale.
    /// </summary>
    public static bool TryEnsureInstalled(
        string lockPath, string destination, Action<string> report, out string? error)
    {
        if (!ToolchainLock.TryRead(lockPath, out ToolchainLock? lockFile, out error) || lockFile is null)
        {
            return false;
        }

        string rid = PlayerStub.HostRid;
        if (!lockFile.TryGetAsset(rid, out string url, out string sha256))
        {
            error = $"No emulator runtime is pinned for platform '{rid}' in {lockPath}.";
            return false;
        }

        string expectedStamp = $"{lockFile.Version}/{rid}";

        if (ToolchainFetcher.ReadStamp(destination) == expectedStamp &&
            MissingFile(destination) is null)
        {
            report($"GB# emulator runtime {lockFile.Version} already present at {destination}");
            return true;
        }

        report($"Fetching GB# emulator runtime {lockFile.Version} ({rid})");

        if (!ToolchainFetcher.TryFetch(url, sha256, destination, report, out error))
        {
            return false;
        }

        ToolchainFetcher.MarkExecutable(Path.Combine(destination, "bin"));

        if (MissingFile(destination) is { } missing)
        {
            error = $"'{missing}' is missing from the emulator archive. Its layout may have changed.";
            return false;
        }

        ToolchainFetcher.WriteStamp(destination, expectedStamp);

        report($"GB# emulator runtime {lockFile.Version} installed to {destination}");
        return true;
    }

    /// <summary>
    /// The two library flavours the runtime ships, the header they implement,
    /// and the Player. Checked after a fetch rather than at the point of use:
    /// a file missing from one platform's archive is otherwise discovered as
    /// a P/Invoke failure on that platform alone, which is the most expensive
    /// place to find it.
    /// </summary>
    private static string? MissingFile(string root)
    {
        foreach (EmulatorFlavour flavour in new[] { EmulatorFlavour.Fast, EmulatorFlavour.Debug })
        {
            string library = Path.Combine(root, "bin", EmulatorRuntime.LibraryFileName(flavour));
            if (!File.Exists(library))
            {
                return library;
            }
        }

        string player = Path.Combine(root, "bin", PlayerStub.PlayerFileName(PlayerStub.HostRid));
        if (!File.Exists(player))
        {
            return player;
        }

        string header = Path.Combine(root, "include", "gbsharp.h");
        return File.Exists(header) ? null : header;
    }
}
