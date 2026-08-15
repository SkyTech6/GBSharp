namespace GB;

/// <summary>One of the four sound channels.</summary>
public enum Channel : byte
{
    /// <summary>Square wave with a frequency sweep.</summary>
    Pulse1 = 1,

    /// <summary>Square wave without a sweep.</summary>
    Pulse2 = 2,

    /// <summary>Plays a 32-sample waveform from wave RAM.</summary>
    Wave = 3,

    /// <summary>Pseudo-random noise, for percussion and effects.</summary>
    Noise = 4,
}

/// <summary>How much of a square wave's period is spent high.</summary>
public enum Duty : byte
{
    Eighth = 0x00,
    Quarter = 0x40,
    Half = 0x80,
    ThreeQuarters = 0xC0,
}

/// <summary>
/// A pitch, as the value the frequency registers actually take.
/// </summary>
/// <remarks>
/// <para>
/// These are periods, not frequencies: the hardware wants
/// <c>2048 - 131072/Hz</c>, and that is what the members hold. Higher values
/// are higher pitches, and the spacing is not linear.
/// </para>
/// <para>
/// The enum is backed by <c>ushort</c> so a note used at a call site folds to a
/// literal during lowering. There is no note table in ROM and no lookup at
/// runtime: writing <c>Note.A4</c> costs exactly what writing 1750 would.
/// </para>
/// </remarks>
public enum Note : ushort
{
    C3 = 1046, Cs3 = 1102, D3 = 1155, Ds3 = 1205, E3 = 1253, F3 = 1297,
    Fs3 = 1339, G3 = 1379, Gs3 = 1417, A3 = 1452, As3 = 1486, B3 = 1517,

    C4 = 1547, Cs4 = 1575, D4 = 1602, Ds4 = 1627, E4 = 1650, F4 = 1673,
    Fs4 = 1694, G4 = 1714, Gs4 = 1732, A4 = 1750, As4 = 1767, B4 = 1783,

    C5 = 1798, Cs5 = 1812, D5 = 1825, Ds5 = 1837, E5 = 1849, F5 = 1860,
    Fs5 = 1871, G5 = 1881, Gs5 = 1890, A5 = 1899, As5 = 1907, B5 = 1915,

    C6 = 1923, Cs6 = 1930, D6 = 1936, Ds6 = 1943, E6 = 1949, F6 = 1954,
    Fs6 = 1959, G6 = 1964, Gs6 = 1969, A6 = 1974, As6 = 1978, B6 = 1982,
}

/// <summary>
/// The sound hardware, at register level.
/// </summary>
/// <remarks>
/// This is deliberately not a music engine. It writes the APU registers and
/// stops there: a note plays until something stops it, nothing is sequenced,
/// and there is no mixing. A game that wants music wants a tracker driver, and
/// that is a library rather than a framework concern.
/// </remarks>
public static class Audio
{
    /// <summary>
    /// Powers up the APU and enables all channels on both speakers.
    /// </summary>
    /// <remarks>
    /// The sound hardware ignores every other register while it is off, so this
    /// has to come first.
    /// </remarks>
    [Native("gbs_audio_on")]
    public static void Enable() => throw FrameworkOnly.Declaration();

    /// <summary>Powers down the APU, silencing everything and clearing its registers.</summary>
    [Native("gbs_audio_off")]
    public static void Disable() => throw FrameworkOnly.Declaration();

    /// <summary>Sets the master volume for each speaker, 0-7.</summary>
    [Native("gbs_audio_master_volume")]
    public static void SetMasterVolume(byte left, byte right) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Sets which channels reach which speaker.
    /// </summary>
    /// <remarks>
    /// The low nibble routes channels 1-4 to the right speaker, the high nibble
    /// to the left. 0xFF is everything on both.
    /// </remarks>
    [Native("gbs_audio_routing")]
    public static void SetRouting(byte mask) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Starts a note on a square-wave channel.
    /// </summary>
    /// <remarks>
    /// <paramref name="channel"/> is a runtime value, so this costs a branch in
    /// the shim on top of the register writes. Only
    /// <see cref="Channel.Pulse1"/> and <see cref="Channel.Pulse2"/> do
    /// anything; the note plays until <see cref="Stop"/>.
    /// </remarks>
    [Native("gbs_audio_tone")]
    public static void PlayTone(Channel channel, Note note, byte volume, Duty duty) =>
        throw FrameworkOnly.Declaration();

    /// <summary>
    /// Starts noise on channel 4.
    /// </summary>
    /// <remarks>
    /// <paramref name="period"/> is the raw polynomial-counter byte: lower
    /// values are brighter. Useful for hits and explosions.
    /// </remarks>
    [Native("gbs_audio_noise")]
    public static void PlayNoise(byte volume, byte period) => throw FrameworkOnly.Declaration();

    /// <summary>Silences one channel by dropping its envelope to zero.</summary>
    [Native("gbs_audio_stop")]
    public static void Stop(Channel channel) => throw FrameworkOnly.Declaration();
}
