using GBSharp.Assets.Images;

namespace GBSharp.Tests.Assets;

/// <summary>
/// Writes minimal PNGs in memory, for tests.
/// </summary>
/// <remarks>
/// <para>
/// A thin alias over <see cref="PngEncoder"/>, which now lives in
/// <c>GBSharp.Assets</c> because <c>gbsharp new</c> needs it too: a template that
/// synthesises its placeholder art ships no binaries. This name stays so the
/// tests keep reading as tests.
/// </para>
/// <para>
/// Better than checked-in fixtures for most of what these tests need. The intent
/// stays readable: <c>TestPng.Indexed(16, 8, sixColours)</c> says what the case
/// is, where a binary file says nothing, and cases like "300 distinct tiles" or
/// "17 pixels wide" are one line rather than an art asset. It also exercises the
/// decoder against an independent encoder, which catches filter and stride
/// mistakes a single hand-made fixture would not.
/// </para>
/// </remarks>
public static class TestPng
{
    /// <inheritdoc cref="PngEncoder.Rgb"/>
    public static byte[] Rgb(int width, int height, Func<int, int, Rgba32> pixel) =>
        PngEncoder.Rgb(width, height, pixel);

    /// <inheritdoc cref="PngEncoder.Indexed"/>
    public static byte[] Indexed(int width, int height, Rgba32[] palette, Func<int, int, byte> index) =>
        PngEncoder.Indexed(width, height, palette, index);

    /// <inheritdoc cref="PngEncoder.Grey"/>
    public static byte[] Grey(int width, int height, int bitDepth, Func<int, int, byte> value) =>
        PngEncoder.Grey(width, height, bitDepth, value);

    /// <inheritdoc cref="PngEncoder.Interlaced"/>
    public static byte[] Interlaced(int width, int height) =>
        PngEncoder.Interlaced(width, height);
}
