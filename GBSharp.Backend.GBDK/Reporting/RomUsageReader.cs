using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GBSharp.Backend.GBDK.Toolchain;

namespace GBSharp.Backend.GBDK.Reporting;

/// <summary>Which memory a bank lives in.</summary>
public enum MemoryRegion
{
    Rom,
    Wram,
    Sram,
    Other,
}

/// <param name="Name">The linker's own area name, e.g. <c>ROM_0</c>.</param>
/// <param name="BankNumber">The bank index within its region.</param>
public sealed record BankUsage(
    string Name,
    MemoryRegion Region,
    int BankNumber,
    int Size,
    int Used)
{
    public int Free => Size - Used;

    public int UsedPercent => Size == 0 ? 0 : (int)(Used * 100L / Size);
}

/// <summary>What the linker actually placed, as opposed to what GB# declared.</summary>
public sealed record RomUsageReport(IReadOnlyList<BankUsage> Banks)
{
    public IEnumerable<BankUsage> Rom => Banks.Where(b => b.Region == MemoryRegion.Rom);

    public IEnumerable<BankUsage> Wram => Banks.Where(b => b.Region == MemoryRegion.Wram);

    public int RomUsed => Rom.Sum(b => b.Used);

    public int WramUsed => Wram.Sum(b => b.Used);

    public int WramSize => Wram.Sum(b => b.Size);
}

/// <summary>
/// Reads per-bank usage out of a linker map.
/// </summary>
/// <remarks>
/// <para>
/// This shells out to GBDK's own <c>romusage</c> rather than parsing sdld's map
/// format directly. The tool ships inside the pinned toolchain, already knows
/// the difference between GBDK and RGBDS maps, already understands merged banks
/// and the WRAM and SRAM regions, and has a machine-readable output mode. A
/// hand-written area parser would be a second implementation of something that
/// is already installed, and would go wrong in ways this one has not.
/// </para>
/// <para>
/// Its absence is never fatal. The ROM is built either way; only the report is
/// poorer, and GBS0510 says so.
/// </para>
/// </remarks>
public static class RomUsageReader
{
    public static bool TryRead(
        GbdkToolchain toolchain,
        string mapPath,
        out RomUsageReport? report,
        out string? failure)
    {
        report = null;

        if (!File.Exists(toolchain.RomUsage))
        {
            failure = $"'{Path.GetFileName(toolchain.RomUsage)}' is not in this GBDK install";
            return false;
        }

        if (!File.Exists(mapPath))
        {
            failure = "the linker did not produce a map file";
            return false;
        }

        string json;

        try
        {
            // -sJ is JSON, -Q suppresses the banner that would precede it.
            var startInfo = new ProcessStartInfo(toolchain.RomUsage)
            {
                WorkingDirectory = Path.GetDirectoryName(mapPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add(Path.GetFileName(mapPath));
            startInfo.ArgumentList.Add("-sJ");
            startInfo.ArgumentList.Add("-Q");

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                failure = "romusage could not be started";
                return false;
            }

            json = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                failure = $"romusage exited with code {process.ExitCode}";
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = e.Message;
            return false;
        }

        return TryParse(json, out report, out failure);
    }

    /// <summary>
    /// Parses romusage's JSON.
    /// </summary>
    /// <remarks>
    /// Every numeric field arrives as a quoted string, so the whole document is
    /// read as strings and converted here. Deserialising straight into an int
    /// looks right and throws at runtime.
    /// </remarks>
    public static bool TryParse(string json, out RomUsageReport? report, out string? failure)
    {
        report = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("banks", out JsonElement banks) ||
                banks.ValueKind != JsonValueKind.Array)
            {
                failure = "romusage produced no bank list";
                return false;
            }

            var parsed = new List<BankUsage>();

            foreach (JsonElement bank in banks.EnumerateArray())
            {
                string name = Text(bank, "name") ?? "?";

                parsed.Add(new BankUsage(
                    name,
                    RegionOf(name),
                    BankNumberOf(name, bank),
                    Number(bank, "size"),
                    Number(bank, "used")));
            }

            report = new RomUsageReport(parsed);
            failure = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException)
        {
            failure = "romusage output could not be read: " + e.Message;
            return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 } text
            ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>
    /// The bank a reported area belongs to, taken from its name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The area name is the authority. romusage also reports
    /// <c>baseBankNum</c>, and the two agree for a contiguous layout, but they
    /// diverge as soon as a bank is skipped: a ROM with code in bank 0 and data
    /// in bank 2 reports the second area as <c>ROM_2</c> with
    /// <c>baseBankNum</c> 1, because that field counts the areas present rather
    /// than naming the bank. Trusting it labelled bank 2's contents as bank 1 in
    /// the build report, which is exactly the kind of layout confusion banking
    /// is supposed to remove.
    /// </para>
    /// <para>
    /// Falls back to <c>baseBankNum</c> for an area whose name carries no
    /// number, such as <c>WRAM_LO</c>.
    /// </para>
    /// </remarks>
    private static int BankNumberOf(string name, JsonElement bank)
    {
        int underscore = name.LastIndexOf('_');

        if (underscore >= 0 &&
            int.TryParse(
                name.AsSpan(underscore + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int fromName))
        {
            return fromName;
        }

        return Number(bank, "baseBankNum");
    }

    private static MemoryRegion RegionOf(string name)
    {
        if (name.StartsWith("ROM", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryRegion.Rom;
        }

        if (name.StartsWith("WRAM", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("HRAM", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryRegion.Wram;
        }

        return name.StartsWith("SRAM", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("XRAM", StringComparison.OrdinalIgnoreCase)
            ? MemoryRegion.Sram
            : MemoryRegion.Other;
    }
}
