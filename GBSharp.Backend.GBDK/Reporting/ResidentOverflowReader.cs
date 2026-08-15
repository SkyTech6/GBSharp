using System.Globalization;
using System.Text.RegularExpressions;

namespace GBSharp.Backend.GBDK.Reporting;

/// <param name="Name">The linker's own area name, e.g. <c>_CODE</c>.</param>
/// <param name="Start">The area's first address, as the linker placed it.</param>
/// <param name="Size">The area's length in bytes.</param>
public sealed record LinkedArea(string Name, int Start, int Size)
{
    /// <summary>One past the area's last byte.</summary>
    public int End => Start + Size;
}

/// <param name="Crossing">The area the bank boundary falls inside, or the first one wholly past it.</param>
/// <param name="ResidentEnd">Where the resident chain actually ends.</param>
public sealed record ResidentOverflow(LinkedArea Crossing, int ResidentEnd)
{
    public int Bytes => ResidentEnd - ResidentOverflowReader.ResidentBankEnd;
}

/// <summary>
/// Finds a resident bank that the linker ran past the end of.
/// </summary>
/// <remarks>
/// <para>
/// This parses sdld's map directly, which everything else in this namespace
/// deliberately does not do: <see cref="RomUsageReader"/> shells out to GBDK's
/// <c>romusage</c> instead, and should keep doing so. The exception is here
/// because romusage structurally cannot answer this question: it buckets bytes
/// by the address they occupy, so an area that starts in bank 0 and runs past
/// <c>0x4000</c> is reported as bank 0 being nearly full and the switchable
/// bank holding some content. Both halves look ordinary. A ROM whose
/// <c>_CODE</c> ended at <c>0x48D9</c> reported bank 0 at 97% and bank 1 at
/// 25%, and booted to a white screen, because everything above <c>0x4000</c>
/// was overlaid by whichever bank was switched in.
/// </para>
/// <para>
/// Neither lcc nor sdld says anything: the linker places areas in order and
/// stops caring where they land. So the only account of this failure is the
/// area table, and the only way to read the area table is to read it.
/// </para>
/// <para>
/// An area counts as resident when it starts below <c>0xA000</c>. Above that
/// are SRAM, WRAM and HRAM, which are not ROM at all; banked code is placed at
/// a flat offset far above it (bank 1 lands at <c>0x14000</c>), so it is
/// excluded by the same test. That leaves exactly the chain the linker fills
/// bank 0 with. The check is on where the chain ends rather than on any single
/// area, because the boundary can fall between two areas as easily as inside
/// one, and an overflow of nothing but wholly-displaced areas is still an
/// overflow.
/// </para>
/// </remarks>
public static class ResidentOverflowReader
{
    /// <summary>The first address past the resident bank.</summary>
    public const int ResidentBankEnd = 0x4000;

    /// <summary>The first address that is no longer ROM.</summary>
    private const int RomEnd = 0xA000;

    // "_CODE                  00000200    000046D9 =       18137. bytes (REL,CON)"
    // Addr and Size are the authority; the decimal column repeats Size.
    private static readonly Regex AreaLine = new(
        @"^(?<name>\S+)\s+(?<addr>[0-9A-Fa-f]{4,8})\s+(?<size>[0-9A-Fa-f]{4,8})\s*=\s*\d+\.\s*bytes",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The overflow of the resident bank, or null when the map is missing,
    /// unreadable, or describes a ROM that fits.
    /// </summary>
    public static ResidentOverflow? Read(string mapPath)
    {
        if (!File.Exists(mapPath))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadLines(mapPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A report this build cannot produce is not a build failure; the
            // ROM is already written either way, and GBS0510 covers the map
            // being unavailable.
            return null;
        }
    }

    /// <summary>Reads an already-open map, for tests and for <see cref="Read"/>.</summary>
    public static ResidentOverflow? Parse(IEnumerable<string> mapLines)
    {
        LinkedArea? crossing = null;
        int residentEnd = 0;

        foreach (string line in mapLines)
        {
            Match match = AreaLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            int start = Hex(match.Groups["addr"].Value);
            if (start >= RomEnd)
            {
                continue;
            }

            var area = new LinkedArea(match.Groups["name"].Value, start, Hex(match.Groups["size"].Value));
            residentEnd = Math.Max(residentEnd, area.End);

            // The map lists an area once per contributing module, so the first
            // one past the boundary wins and the repeats change nothing.
            if (area.End > ResidentBankEnd &&
                (crossing is null || area.Start < crossing.Start))
            {
                crossing = area;
            }
        }

        return crossing is null || residentEnd <= ResidentBankEnd
            ? null
            : new ResidentOverflow(crossing, residentEnd);
    }

    private static int Hex(string text) =>
        int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
