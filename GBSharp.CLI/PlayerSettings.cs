using System.Text.Json;
using System.Text.Json.Serialization;

namespace GBSharp.Cli;

/// <summary>
/// How a published game presents itself.
/// </summary>
/// <remarks>
/// <para>
/// These are written into the published executable by <c>gbsharp publish</c> and
/// read back by the Player at startup. The Player has no settings screen and no
/// configuration file of its own: a game decides how it looks, and a player that
/// could disagree with the game would be an emulator wearing the game's name.
/// </para>
/// <para>
/// Every property is nullable so that "not mentioned" and "set to the default"
/// stay different things. Only what the game actually said is serialized, which
/// keeps the payload readable and lets the Player's own defaults move without
/// silently changing games that never expressed an opinion.
/// </para>
/// </remarks>
public sealed class PlayerSettings
{
    /// <summary>The window title. Defaults to the project name.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Window size as a multiple of 160x144. 1 to 8.</summary>
    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

    /// <summary>Open filling the screen.</summary>
    [JsonPropertyName("fullscreen")]
    public bool? Fullscreen { get; set; }

    /// <summary>Let the window be resized. On by default.</summary>
    [JsonPropertyName("resizable")]
    public bool? Resizable { get; set; }

    /// <summary>
    /// Scale by whole numbers only, so every pixel is the same size as every
    /// other pixel. On by default.
    /// </summary>
    [JsonPropertyName("integerScaling")]
    public bool? IntegerScaling { get; set; }

    /// <summary>Output volume, 0 to 100.</summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; set; }

    /// <summary>
    /// The JSON the Player reads, with the project name filled in as the title
    /// when the game did not name one.
    /// </summary>
    /// <remarks>
    /// A game that says nothing still gets a title, because the alternative is a
    /// window called "GB# Player", which tells the person playing it nothing
    /// about what they are playing.
    /// </remarks>
    public static string Serialize(PlayerSettings? settings, string projectName)
    {
        var effective = new PlayerSettings
        {
            Title = string.IsNullOrWhiteSpace(settings?.Title) ? projectName : settings.Title,
            Scale = settings?.Scale,
            Fullscreen = settings?.Fullscreen,
            Resizable = settings?.Resizable,
            IntegerScaling = settings?.IntegerScaling,
            Volume = settings?.Volume,
        };

        return JsonSerializer.Serialize(effective, SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
