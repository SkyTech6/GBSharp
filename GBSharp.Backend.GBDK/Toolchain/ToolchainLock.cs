using System.Text.Json;

namespace GBSharp.Backend.GBDK.Toolchain;

/// <summary>
/// A pinned-toolchain lock file: <c>tools/gbdk.lock.json</c> and
/// <c>tools/emulator.lock.json</c> share this shape.
/// </summary>
/// <remarks>
/// The lock files are the trust anchor of toolchain acquisition (thesis
/// section 21): a URL is fetched only because a checked-in file pins its
/// SHA256. This reader is shared by everything that fetches, so there is one
/// definition of what a valid pin looks like.
/// </remarks>
public sealed class ToolchainLock
{
    private readonly Dictionary<string, (string Url, string Sha256)> _assets;

    private ToolchainLock(string version, Dictionary<string, (string, string)> assets)
    {
        Version = version;
        _assets = assets;
    }

    /// <summary>The pinned release version, e.g. <c>4.5.0</c>.</summary>
    public string Version { get; }

    /// <summary>
    /// Reads a lock file, or says what is wrong with it.
    /// </summary>
    public static bool TryRead(string path, out ToolchainLock? lockFile, out string? error)
    {
        lockFile = null;
        error = null;

        if (!File.Exists(path))
        {
            error = $"Lock file not found: {path}";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            string? version = document.RootElement.TryGetProperty("version", out JsonElement v)
                ? v.GetString()
                : null;

            if (version is null ||
                !document.RootElement.TryGetProperty("assets", out JsonElement assets))
            {
                error = $"{path} is missing its version or assets.";
                return false;
            }

            var parsed = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (JsonProperty asset in assets.EnumerateObject())
            {
                string? url = asset.Value.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
                string? sha = asset.Value.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null;

                if (url is null || sha is null)
                {
                    error = $"The {asset.Name} entry in {path} is missing its url or sha256.";
                    return false;
                }

                parsed[asset.Name] = (url, sha);
            }

            lockFile = new ToolchainLock(version, parsed);
            return true;
        }
        catch (JsonException e)
        {
            error = $"{path} is not valid JSON: {e.Message}";
            return false;
        }
    }

    /// <summary>The pinned archive for a platform key, when one exists.</summary>
    public bool TryGetAsset(string key, out string url, out string sha256)
    {
        if (_assets.TryGetValue(key, out (string Url, string Sha256) asset))
        {
            url = asset.Url;
            sha256 = asset.Sha256;
            return true;
        }

        url = string.Empty;
        sha256 = string.Empty;
        return false;
    }

    /// <summary>
    /// Finds a lock file the way toolchain locators find installs: the
    /// <c>tools/</c> directory of an enclosing checkout first, then packed
    /// beside the running assembly, which is where the <c>gbsharp</c> tool
    /// package carries its copies.
    /// </summary>
    public static string? Find(string fileName)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "tools", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        string packaged = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(packaged) ? packaged : null;
    }
}
