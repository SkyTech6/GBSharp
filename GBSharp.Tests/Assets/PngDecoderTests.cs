using GBSharp.Assets.Images;

namespace GBSharp.Tests.Assets;

/// <summary>
/// The PNG decoder. These run without GBDK and without any image library.
/// </summary>
public sealed class PngDecoderTests
{
    private static readonly Rgba32 Red = new(255, 0, 0, 255);
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);

    [Fact]
    public void ReadsTruecolour()
    {
        byte[] png = TestPng.Rgb(4, 2, (x, y) => x == y ? Red : Blue);

        Assert.True(PngDecoder.TryDecode(png, out DecodedImage? image, out PngFailure? failure), failure?.Message);
        Assert.Equal(4, image!.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(Red, image[0, 0]);
        Assert.Equal(Blue, image[1, 0]);
        Assert.Equal(Red, image[1, 1]);
    }

    [Fact]
    public void ReadsIndexedAndKeepsPaletteOrder()
    {
        // The PLTE order is the artist saying which colour is which shade, so
        // it has to survive decoding rather than being re-derived.
        Rgba32[] palette = [Blue, Red];
        byte[] png = TestPng.Indexed(2, 1, palette, (x, _) => (byte)x);

        Assert.True(PngDecoder.TryDecode(png, out DecodedImage? image, out _));
        Assert.Equal(Blue, image![0, 0]);
        Assert.Equal(Red, image[1, 0]);
        Assert.Equal(palette, image.SourcePalette);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ReadsEveryGreyscaleBitDepth(int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        byte[] png = TestPng.Grey(8, 1, bitDepth, (x, _) => (byte)(x % (max + 1)));

        Assert.True(PngDecoder.TryDecode(png, out DecodedImage? image, out PngFailure? failure), failure?.Message);

        // Sub-byte depths are scaled to 0-255, so compare the pattern rather
        // than the value: pixel 0 is black and the rest ascend from it.
        Assert.Equal(0, image![0, 0].R);
        Assert.True(image[1, 0].R > image[0, 0].R, "brightness should increase across the row");
    }

    [Fact]
    public void RejectsInterlacingWithAnActionableMessage()
    {
        byte[] png = TestPng.Interlaced(8, 8);

        Assert.False(PngDecoder.TryDecode(png, out _, out PngFailure? failure));
        Assert.True(failure!.IsUnsupportedFeature, "interlacing is a feature, not corruption");
        Assert.Contains("interlac", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsSomethingThatIsNotAPngAtAll()
    {
        Assert.False(PngDecoder.TryDecode("not a png at all"u8.ToArray(), out _, out PngFailure? failure));
        Assert.False(failure!.IsUnsupportedFeature);
    }

    [Fact]
    public void TruncationAtAnyPointIsADiagnosticAndNeverAnException()
    {
        // The input is a file someone may be saving while the build reads it.
        // Every prefix of a valid PNG has to come back as a failure rather than
        // as an unhandled exception out of the middle of the decoder.
        byte[] png = TestPng.Rgb(16, 16, (x, y) => x + y > 8 ? Red : Blue);

        for (int length = 0; length < png.Length; length++)
        {
            byte[] truncated = png[..length];

            bool decoded = PngDecoder.TryDecode(truncated, out DecodedImage? image, out PngFailure? failure);

            Assert.False(decoded, $"a {length}-byte prefix should not decode");
            Assert.Null(image);
            Assert.NotNull(failure);
        }
    }

    [Fact]
    public void CorruptionIsCaughtByTheChecksum()
    {
        byte[] png = TestPng.Rgb(8, 8, (_, _) => Red);

        // Flip a bit inside the image data, past the signature and header.
        png[^12] ^= 0xFF;

        Assert.False(PngDecoder.TryDecode(png, out _, out PngFailure? failure));
        Assert.NotNull(failure);
    }
}
