namespace GBSharp.Assets.Tiles;

/// <summary>Where one cell's artwork ended up in the deduplicated tileset.</summary>
public readonly record struct TilePlacement(int Index, bool FlipX, bool FlipY);

/// <summary>
/// Collapses repeated tiles so the tileset holds each distinct one once.
/// </summary>
/// <remarks>
/// <para>
/// This is the single biggest saving in the pipeline. A screen of artwork is
/// 360 tiles; almost none of them are distinct, and VRAM only holds 255.
/// </para>
/// <para>
/// Deduplicating mirrored tiles as well is not always available, and the
/// caller decides. A cell can only be drawn flipped if something records the
/// flip: on Game Boy Color the attribute map carries flip bits, and OAM carries
/// them for sprites, but an original Game Boy's background map is one byte per
/// cell with nowhere to put them. Turning it on there would silently produce a
/// wrong image, so it is a caller decision rather than a default.
/// </para>
/// </remarks>
public sealed class TileDeduplicator(bool dedupeFlips)
{
    // Insertion-ordered, because the output order must not depend on hash
    // iteration order: the same image has to convert to the same bytes on
    // Windows and on Linux.
    private readonly List<GbTile> _unique = [];
    private readonly Dictionary<GbTile, TilePlacement> _seen = [];

    public IReadOnlyList<GbTile> Unique => _unique;

    /// <summary>How many tiles were saved specifically by flipping.</summary>
    public int SavedByFlip { get; private set; }

    public TilePlacement Add(GbTile tile)
    {
        if (_seen.TryGetValue(tile, out TilePlacement existing))
        {
            return existing;
        }

        if (dedupeFlips)
        {
            GbTile flipX = tile.FlippedX();
            GbTile flipY = tile.FlippedY();
            GbTile flipXy = flipX.FlippedY();

            foreach ((GbTile candidate, bool x, bool y) in
                     new[] { (flipX, true, false), (flipY, false, true), (flipXy, true, true) })
            {
                if (!_seen.TryGetValue(candidate, out TilePlacement match) || match.FlipX || match.FlipY)
                {
                    continue;
                }

                SavedByFlip++;
                var placement = new TilePlacement(match.Index, x, y);
                _seen[tile] = placement;
                return placement;
            }
        }

        var added = new TilePlacement(_unique.Count, false, false);
        _unique.Add(tile);
        _seen[tile] = added;
        return added;
    }
}
