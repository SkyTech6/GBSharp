namespace GBSharp.Assets.Tiles;

/// <summary>
/// Two 8x8 tiles read together as one 8x16 hardware sprite: top then bottom.
/// </summary>
public readonly record struct TilePair(GbTile Top, GbTile Bottom);

/// <summary>Where one column ended up in the deduplicated pair list.</summary>
public readonly record struct TilePairPlacement(int PairIndex, bool FlipX, bool FlipY);

/// <summary>
/// Collapses repeated 8x16 sprite columns, the same way <see cref="TileDeduplicator"/>
/// collapses repeated 8x8 tiles.
/// </summary>
/// <remarks>
/// Deduplicating a tall sprite tile-by-tile would let its top and bottom land
/// on non-adjacent, non-even-aligned indices, which breaks the hardware's own
/// pairing rule: in 8x16 mode a sprite's tile index has its low bit ignored, so
/// tile 2n and tile 2n+1 must already be that sprite's (top, bottom). Hashing
/// the pair as one 32-byte unit is what keeps that invariant through
/// deduplication.
/// <para>
/// Flipping a whole 8x16 sprite vertically swaps which tile is on top as well
/// as mirroring each one, which is why <see cref="Add"/> builds the flipped
/// candidates from both tiles together rather than delegating to
/// <see cref="GbTile.FlippedY"/> per tile independently.
/// </para>
/// </remarks>
public sealed class TilePairDeduplicator(bool dedupeFlips)
{
    // Insertion-ordered for the same reason as TileDeduplicator: output bytes
    // must not depend on hash iteration order.
    private readonly List<TilePair> _unique = [];
    private readonly Dictionary<TilePair, TilePairPlacement> _seen = [];

    public IReadOnlyList<TilePair> Unique => _unique;

    /// <summary>How many pairs were saved specifically by flipping.</summary>
    public int SavedByFlip { get; private set; }

    public TilePairPlacement Add(TilePair pair)
    {
        if (_seen.TryGetValue(pair, out TilePairPlacement existing))
        {
            return existing;
        }

        if (dedupeFlips)
        {
            // Horizontal flip mirrors each tile in place; vertical flip mirrors
            // each tile and swaps which one is on top.
            var flipX = new TilePair(pair.Top.FlippedX(), pair.Bottom.FlippedX());
            var flipY = new TilePair(pair.Bottom.FlippedY(), pair.Top.FlippedY());
            var flipXy = new TilePair(pair.Bottom.FlippedX().FlippedY(), pair.Top.FlippedX().FlippedY());

            foreach ((TilePair candidate, bool x, bool y) in
                     new[] { (flipX, true, false), (flipY, false, true), (flipXy, true, true) })
            {
                if (!_seen.TryGetValue(candidate, out TilePairPlacement match) || match.FlipX || match.FlipY)
                {
                    continue;
                }

                SavedByFlip++;
                var placement = new TilePairPlacement(match.PairIndex, x, y);
                _seen[pair] = placement;
                return placement;
            }
        }

        var added = new TilePairPlacement(_unique.Count, false, false);
        _unique.Add(pair);
        _seen[pair] = added;
        return added;
    }
}
