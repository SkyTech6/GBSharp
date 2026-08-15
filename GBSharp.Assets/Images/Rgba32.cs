namespace GBSharp.Assets.Images;

/// <summary>One pixel, decoded.</summary>
/// <remarks>
/// Colours are matched exactly rather than approximately, so this is a value
/// with structural equality and no tolerance. Two colours an artist meant to be
/// the same must be the same in the file.
/// </remarks>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    /// <summary>
    /// Perceived brightness, used to order colours into shades when nothing
    /// else says how.
    /// </summary>
    /// <remarks>
    /// Rec. 601 weights, in integer arithmetic. The exact coefficients matter
    /// less than being deterministic: this decides which shade a colour becomes
    /// on an original Game Boy, and it must decide the same way every build.
    /// </remarks>
    public int Luminance => ((R * 299) + (G * 587) + (B * 114)) / 1000;

    /// <summary>
    /// An order that is stable across runs and platforms, brightest highest.
    /// </summary>
    /// <remarks>
    /// Deliberately a <c>long</c>: luminance reaches 255, and shifting that
    /// into bit 31 of an <c>int</c> would make the brightest colours sort as
    /// negative and put black where white belongs.
    /// </remarks>
    public long SortKey => ((long)Luminance << 24) | ((long)R << 16) | ((long)G << 8) | B;

    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}
