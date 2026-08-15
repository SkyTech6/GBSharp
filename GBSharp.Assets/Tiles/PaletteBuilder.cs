using GBSharp.Assets.Images;

namespace GBSharp.Assets.Tiles;

/// <summary>Up to four colours drawn together.</summary>
public sealed record GbPalette(IReadOnlyList<Rgba32> Colors)
{
    /// <summary>
    /// The colours as BGR555, little-endian, always padded to four entries.
    /// </summary>
    /// <remarks>
    /// Each channel is the top five bits of an eight-bit one. The hardware
    /// stores blue highest, which is the opposite of how the colour is written.
    /// </remarks>
    public byte[] ToBgr555()
    {
        var bytes = new byte[8];

        for (int i = 0; i < 4; i++)
        {
            Rgba32 color = i < Colors.Count ? Colors[i] : new Rgba32(0, 0, 0, 255);

            int value = ((color.R >> 3) & 0x1F)
                      | (((color.G >> 3) & 0x1F) << 5)
                      | (((color.B >> 3) & 0x1F) << 10);

            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[(i * 2) + 1] = (byte)(value >> 8);
        }

        return bytes;
    }
}

/// <summary>Why an image could not be reduced to Game Boy palettes.</summary>
public abstract record PaletteFailure;

public sealed record TooManyColors(int Count) : PaletteFailure;

public sealed record TileTooManyColors(int TileX, int TileY, int Count) : PaletteFailure;

public sealed record TooManyPalettes(int Count) : PaletteFailure;

/// <param name="PixelIndices">One index per pixel, 0-3 within that pixel's tile palette.</param>
/// <param name="TilePalettes">One palette number per tile, row-major.</param>
public sealed record IndexedImage(
    byte[] PixelIndices,
    byte[] TilePalettes,
    IReadOnlyList<GbPalette> Palettes);

/// <summary>
/// Reduces an image to indices and palettes.
/// </summary>
/// <remarks>
/// The two machines need genuinely different work. An original Game Boy has one
/// palette for the whole image and four shades to spend; a Game Boy Color has
/// eight palettes of four real colours, assigned per tile, so the job becomes
/// partitioning the tiles' colour sets into at most eight groups.
/// </remarks>
public static class PaletteBuilder
{
    public const int ColorsPerPalette = 4;
    public const int MaxColorPalettes = 8;

    /// <summary>
    /// One palette for the whole image, ordered so index 0 is the lightest.
    /// </summary>
    /// <remarks>
    /// Lightest first matches the hardware's default background palette, so an
    /// image renders sensibly before any palette is set. An indexed PNG keeps
    /// its own PLTE order instead: that is the artist stating which colour is
    /// which shade, and second-guessing it would make the result depend on
    /// brightnesses they may not have thought about.
    /// </remarks>
    public static IndexedImage? BuildMonochrome(
        DecodedImage image,
        int widthTiles,
        int heightTiles,
        out PaletteFailure? failure)
    {
        List<Rgba32> distinct = DistinctColors(image);

        if (distinct.Count > ColorsPerPalette)
        {
            failure = new TooManyColors(distinct.Count);
            return null;
        }

        List<Rgba32> ordered = image.SourcePalette is { Length: > 0 and <= ColorsPerPalette } source
            ? [.. source]
            : [.. distinct.OrderByDescending(c => c.SortKey)];

        var lookup = new Dictionary<Rgba32, byte>();
        for (int i = 0; i < ordered.Count; i++)
        {
            lookup.TryAdd(ordered[i], (byte)i);
        }

        var indices = new byte[image.Width * image.Height];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = lookup.TryGetValue(image.Pixels[i], out byte index) ? index : (byte)0;
        }

        failure = null;
        return new IndexedImage(indices, new byte[widthTiles * heightTiles], [new GbPalette(ordered)]);
    }

    /// <summary>
    /// Groups the image's tiles into at most eight four-colour palettes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every tile must be drawable from one palette, so the first question is
    /// whether any tile uses more than four colours, an artist problem with an
    /// artist fix, which is why the failure names the tile.
    /// </para>
    /// <para>
    /// The grouping itself is a greedy first-fit over the distinct colour sets,
    /// largest first. Optimal set-cover would occasionally fit an image this
    /// misses, but greedy is deterministic, runs in no time, and fails with a
    /// number the artist can act on. Ties break on the sorted colour tuple and
    /// never on hash iteration order, so the same image converts to the same
    /// bytes on every machine.
    /// </para>
    /// </remarks>
    public static IndexedImage? BuildColor(
        DecodedImage image,
        int widthTiles,
        int heightTiles,
        out PaletteFailure? failure)
    {
        var tileColors = new List<Rgba32>[widthTiles * heightTiles];

        for (int ty = 0; ty < heightTiles; ty++)
        {
            for (int tx = 0; tx < widthTiles; tx++)
            {
                List<Rgba32> colors = TileColors(image, tx, ty);

                if (colors.Count > ColorsPerPalette)
                {
                    failure = new TileTooManyColors(tx * GbTile.Size, ty * GbTile.Size, colors.Count);
                    return null;
                }

                tileColors[(ty * widthTiles) + tx] = colors;
            }
        }

        List<List<Rgba32>> palettes = Merge(tileColors);

        if (palettes.Count > MaxColorPalettes)
        {
            failure = new TooManyPalettes(palettes.Count);
            return null;
        }

        var tilePalettes = new byte[widthTiles * heightTiles];
        var pixelIndices = new byte[image.Width * image.Height];

        for (int tile = 0; tile < tileColors.Length; tile++)
        {
            int palette = palettes.FindIndex(p => tileColors[tile].All(p.Contains));
            tilePalettes[tile] = (byte)Math.Max(0, palette);
        }

        for (int ty = 0; ty < heightTiles; ty++)
        {
            for (int tx = 0; tx < widthTiles; tx++)
            {
                List<Rgba32> palette = palettes[tilePalettes[(ty * widthTiles) + tx]];

                for (int y = 0; y < GbTile.Size; y++)
                {
                    for (int x = 0; x < GbTile.Size; x++)
                    {
                        int px = (tx * GbTile.Size) + x;
                        int py = (ty * GbTile.Size) + y;
                        int index = palette.IndexOf(image[px, py]);
                        pixelIndices[(py * image.Width) + px] = (byte)Math.Max(0, index);
                    }
                }
            }
        }

        failure = null;
        return new IndexedImage(
            pixelIndices,
            tilePalettes,
            [.. palettes.Select(p => new GbPalette(p))]);
    }

    /// <summary>Greedy first-fit merge of colour sets into four-colour groups.</summary>
    private static List<List<Rgba32>> Merge(List<Rgba32>[] tileColors)
    {
        List<List<Rgba32>> distinct = [];

        foreach (List<Rgba32> colors in tileColors)
        {
            if (!distinct.Any(existing => colors.All(existing.Contains)))
            {
                distinct.Add(colors);
            }
        }

        // Largest first, ties broken deterministically on the colours themselves.
        distinct.Sort((a, b) =>
        {
            int bySize = b.Count.CompareTo(a.Count);
            return bySize != 0 ? bySize : Key(a).CompareTo(Key(b), StringComparison.Ordinal);
        });

        List<List<Rgba32>> palettes = [];

        foreach (List<Rgba32> colors in distinct)
        {
            List<Rgba32>? target = palettes.FirstOrDefault(p => p.Union(colors).Count() <= ColorsPerPalette);

            if (target is null)
            {
                palettes.Add([.. colors]);
                continue;
            }

            foreach (Rgba32 color in colors.Where(c => !target.Contains(c)))
            {
                target.Add(color);
            }
        }

        return palettes;

        static string Key(List<Rgba32> colors) =>
            string.Join(",", colors.Select(c => c.SortKey).Order());
    }

    private static List<Rgba32> DistinctColors(DecodedImage image)
    {
        List<Rgba32> distinct = [];

        foreach (Rgba32 pixel in image.Pixels)
        {
            if (!distinct.Contains(pixel))
            {
                distinct.Add(pixel);
            }
        }

        return distinct;
    }

    private static List<Rgba32> TileColors(DecodedImage image, int tileX, int tileY)
    {
        List<Rgba32> colors = [];

        for (int y = 0; y < GbTile.Size; y++)
        {
            for (int x = 0; x < GbTile.Size; x++)
            {
                Rgba32 pixel = image[(tileX * GbTile.Size) + x, (tileY * GbTile.Size) + y];

                if (!colors.Contains(pixel))
                {
                    colors.Add(pixel);
                }
            }
        }

        return colors;
    }
}
