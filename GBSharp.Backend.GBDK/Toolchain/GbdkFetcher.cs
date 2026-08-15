using System.Runtime.InteropServices;

namespace GBSharp.Backend.GBDK.Toolchain;

/// <summary>
/// Fetches the pinned GBDK-2020 toolchain, in process.
/// </summary>
/// <remarks>
/// The C# counterpart of <c>tools/get-gbdk.ps1</c>: same lock file, same
/// version stamp, same "check the payload is intact even when the stamp
/// matches" behaviour, so an install made by either is valid to the other.
/// This one exists for <c>gbsharp doctor --fix</c>, which has to work with no
/// checkout and no pwsh anywhere near it.
/// </remarks>
public static class GbdkFetcher
{
    /// <summary>The lock file's asset key for the machine running this.</summary>
    /// <remarks>
    /// GBDK's release assets predate .NET runtime identifiers, hence the
    /// bespoke names. The 32-bit Windows asset is deliberately not reachable:
    /// a process running this code is on a 64-bit .NET, so win64 is always
    /// the right answer there.
    /// </remarks>
    public static string HostAssetKey
    {
        get
        {
            bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;

            if (OperatingSystem.IsWindows())
            {
                return "win64";
            }

            if (OperatingSystem.IsMacOS())
            {
                return arm64 ? "macos-arm64" : "macos";
            }

            return arm64 ? "linux-arm64" : "linux64";
        }
    }

    /// <summary>
    /// Every tool GB# shells out to. Checked after a fetch rather than at the
    /// point of use: a tool missing from one platform's archive is otherwise
    /// discovered as a link failure on that platform alone, which is the most
    /// expensive place to find it.
    /// </summary>
    private static readonly string[] RequiredTools = ["lcc", "bankpack", "romusage"];

    /// <summary>
    /// Makes sure the pinned GBDK is installed at
    /// <paramref name="destination"/>, fetching it if it is missing or stale.
    /// </summary>
    public static bool TryEnsureInstalled(
        string lockPath, string destination, Action<string> report, out string? error)
    {
        if (!ToolchainLock.TryRead(lockPath, out ToolchainLock? lockFile, out error) || lockFile is null)
        {
            return false;
        }

        string key = HostAssetKey;
        if (!lockFile.TryGetAsset(key, out string url, out string sha256))
        {
            error = $"No GBDK asset is pinned for platform '{key}' in {lockPath}.";
            return false;
        }

        string expectedStamp = $"{lockFile.Version}/{key}";

        if (ToolchainFetcher.ReadStamp(destination) == expectedStamp &&
            MissingTool(destination) is null)
        {
            report($"GBDK-2020 {lockFile.Version} already present at {destination}");
            return true;
        }

        report($"Fetching GBDK-2020 {lockFile.Version} ({key})");

        if (!ToolchainFetcher.TryFetch(url, sha256, destination, report, out error))
        {
            return false;
        }

        ToolchainFetcher.MarkExecutable(Path.Combine(destination, "bin"));

        if (MissingTool(destination) is { } missing)
        {
            error = $"'{missing}' is missing from the GBDK archive. Its layout may have changed.";
            return false;
        }

        ToolchainFetcher.WriteStamp(destination, expectedStamp);

        report($"GBDK-2020 {lockFile.Version} installed to {destination}");
        return true;
    }

    private static string? MissingTool(string root)
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        foreach (string tool in RequiredTools)
        {
            string path = Path.Combine(root, "bin", tool + extension);
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
