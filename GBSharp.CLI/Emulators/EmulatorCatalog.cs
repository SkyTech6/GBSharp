namespace GBSharp.Cli.Emulators;

/// <summary>
/// An emulator GB# knows how to launch.
/// </summary>
/// <param name="Id">What a developer writes in configuration.</param>
/// <param name="DisplayName">What the terminal calls it.</param>
/// <param name="ExecutableNames">Names to look for on PATH and in install directories.</param>
/// <param name="ArgumentTemplate">
/// Arguments, with <c>{rom}</c>, <c>{romDir}</c>, <c>{sym}</c>, <c>{map}</c> and
/// <c>{name}</c> substituted.
/// </param>
/// <param name="LoadsSymbolsAutomatically">
/// Whether it finds the <c>.sym</c> beside the ROM by itself.
/// </param>
public sealed record KnownEmulator(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ExecutableNames,
    string ArgumentTemplate,
    bool LoadsSymbolsAutomatically);

/// <summary>
/// The emulators GB# can find and launch.
/// </summary>
/// <remarks>
/// <para>
/// Short on purpose. These are the ones a Game Boy developer is likely to have,
/// and the list exists so <c>gbsharp run</c> works without configuration rather
/// than to be exhaustive: any other emulator still works by giving its path.
/// </para>
/// <para>
/// The symbol column matters more than it looks. <c>RomBuilder</c> already leaves
/// a <c>.sym</c> beside the ROM, and the two emulators with real debuggers pick it
/// up on their own, so source-level debugging needs no work here at all, only
/// saying that it is available.
/// </para>
/// </remarks>
public static class EmulatorCatalog
{
    public static readonly IReadOnlyList<KnownEmulator> Known =
    [
        new(
            "sameboy",
            "SameBoy",
            ["sameboy", "SameBoy", "sameboy_sdl"],
            "{rom}",
            LoadsSymbolsAutomatically: true),

        new(
            "bgb",
            "BGB",
            ["bgb64", "bgb"],
            "{rom}",
            LoadsSymbolsAutomatically: true),

        new(
            "emulicious",
            "Emulicious",
            ["Emulicious"],
            "{rom}",
            LoadsSymbolsAutomatically: true),

        new(
            "mgba",
            "mGBA",
            ["mgba", "mgba-qt"],
            "{rom}",
            LoadsSymbolsAutomatically: false),
    ];

    public static KnownEmulator? ById(string? id) =>
        id is null ? null : Known.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits a template into arguments, then substitutes.
    /// </summary>
    /// <remarks>
    /// The order is the whole point. Splitting first and substituting per token
    /// means a ROM path containing a space stays one argument; substituting first
    /// and splitting after would tear it in half at the space: the classic
    /// version of this bug, and one that only shows up on the machines whose
    /// user name has a space in it.
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(string template, string romPath)
    {
        string romDirectory = Path.GetDirectoryName(romPath) ?? ".";
        string withoutExtension = Path.Combine(
            romDirectory,
            Path.GetFileNameWithoutExtension(romPath));

        var arguments = new List<string>();

        foreach (string token in SplitRespectingQuotes(template))
        {
            arguments.Add(token
                .Replace("{rom}", romPath, StringComparison.Ordinal)
                .Replace("{romDir}", romDirectory, StringComparison.Ordinal)
                .Replace("{sym}", withoutExtension + ".sym", StringComparison.Ordinal)
                .Replace("{map}", withoutExtension + ".map", StringComparison.Ordinal)
                .Replace("{name}", Path.GetFileNameWithoutExtension(romPath), StringComparison.Ordinal));
        }

        return arguments;
    }

    private static IEnumerable<string> SplitRespectingQuotes(string template)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (char c in template)
        {
            switch (c)
            {
                case '"':
                    quoted = !quoted;
                    break;

                case ' ' when !quoted:
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }

                    break;

                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
