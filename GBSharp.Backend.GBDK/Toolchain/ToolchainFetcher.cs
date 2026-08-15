using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GBSharp.Backend.GBDK.Toolchain;

/// <summary>
/// Downloads a pinned archive, verifies it, and unpacks it into place.
/// </summary>
/// <remarks>
/// <para>
/// The C# port of the download → SHA256-verify → extract dance that
/// <c>tools/get-gbdk.ps1</c> and <c>tools/get-emulator.ps1</c> perform. Those
/// scripts stay: CI depends on them and pwsh is guaranteed there. This exists
/// for the packaged <c>gbsharp</c> tool, which runs on machines with no
/// checkout and no pwsh, and it is the one implementation everything
/// in-process shares rather than a third copy of the dance.
/// </para>
/// <para>
/// Nothing is written anywhere a later run could trust until the hash has
/// been verified, and extraction is staged beside the destination and renamed
/// into place, so a failed download or a killed process never leaves a
/// half-unpacked toolchain behind.
/// </para>
/// </remarks>
public static class ToolchainFetcher
{
    /// <summary>
    /// The stamp file recording which pinned version an install came from.
    /// Same name and format as the fetch scripts write, so either acquisition
    /// path can validate the other's install.
    /// </summary>
    public const string StampFileName = ".gbsharp-version";

    /// <summary>
    /// Fetches <paramref name="url"/>, verifies it against
    /// <paramref name="expectedSha256"/>, and unpacks it to
    /// <paramref name="destination"/>, replacing whatever was there.
    /// </summary>
    /// <param name="report">Progress, so a download does not look like a hang.</param>
    public static bool TryFetch(
        string url, string expectedSha256, string destination, Action<string> report, out string? error)
    {
        error = null;
        string temporary = Path.Combine(Path.GetTempPath(), "gbsharp-fetch-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temporary);
            string archive = Path.Combine(temporary, Path.GetFileName(new Uri(url).LocalPath));

            report($"  fetching {url}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                using Stream response = client.GetStreamAsync(url).GetAwaiter().GetResult();
                using FileStream file = File.Create(archive);
                response.CopyTo(file);
            }

            // Verified before it is unpacked, not after: an archive that fails
            // this is never written anywhere a later run could pick it up.
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = $"SHA256 mismatch for {url}.\n  expected: {expectedSha256}\n  actual:   {actual}";
                return false;
            }

            report("  sha256 verified");

            // Staged beside the destination rather than in the temporary
            // directory, because the two are routinely on different drives and
            // Directory.Move cannot cross a volume. Unpacking somewhere that
            // can be renamed into place is also what keeps a failed extraction
            // from leaving a half-unpacked toolchain a later run would trust.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            string staging = destination + ".incoming";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);

            Extract(archive, staging);

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.Move(SinglePayloadDirectory(staging), destination);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            error = $"The archive could not be fetched from {url}: {e.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing over.
            }
        }
    }

    /// <summary>Reads the version stamp of an install, if one was written.</summary>
    public static string? ReadStamp(string installDirectory)
    {
        string path = Path.Combine(installDirectory, StampFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>Records which pinned version an install came from.</summary>
    public static void WriteStamp(string installDirectory, string stamp) =>
        File.WriteAllText(Path.Combine(installDirectory, StampFileName), stamp);

    /// <summary>
    /// Restores the executable bit on everything in a directory, which zip
    /// extraction drops on Unix. A no-op on Windows.
    /// </summary>
    public static void MarkExecutable(string directory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            File.SetUnixFileMode(
                file,
                File.GetUnixFileMode(file) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static void Extract(string archive, string destination)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }

        using FileStream compressed = File.OpenRead(archive);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    /// <summary>
    /// Tolerates an archive that wraps its contents in one directory, the way
    /// tools/get-gbdk.ps1 does for the same reason.
    /// </summary>
    private static string SinglePayloadDirectory(string staging)
    {
        string[] entries = Directory.GetFileSystemEntries(staging);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;
    }
}
