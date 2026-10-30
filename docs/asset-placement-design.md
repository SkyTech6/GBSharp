# Converted asset placement and map copying

The original zero-based `Load` and `Background.DrawRegion` calls remain unchanged.
Explicit placement is opt-in. Converted asset expansion retains its existing ABI
prefix: map assets expand to nine native arguments, sprite sheets to five, and
metasprites to eight. Additional placement arguments follow that prefix.

```csharp
Background.LoadGraphics(World, 0, 0);
Window.LoadGraphics(Hud, 128, 1);
Sprites.Load(Hero, 192, 0);
Metasprites.Load(Enemy, 196, 1);
Background.CopyRegion(World, 0, 0, 20, 18, 0, 0, 0, 0);
Window.CopyRegion(Hud, 0, 0, 20, 2, 0, 0, 128, 1);
Metasprites.Move(Enemy, 0, 196, 4, 100, 80, 1);
```

The example assumes the world's converted tile count fits 0–127, HUD fits
128–191, hero fits 192–195, and enemy fits 196–255; each asset uses one palette.
OAM 0–3 is reserved for the hero and 4 onward for the enemy. The calling game must
size these reservations from its actual converted counts. The scrolling sample
uses concrete smaller reservations and a font for its HUD.

Public signatures:

- `Background.Load(TileMap map, byte firstTile, byte firstPalette)` and the same
  overload on `Window`: upload graphics and copy the complete map (at most 32×32).
- `Background.LoadGraphics(TileMap map, byte firstTile, byte firstPalette)` and
  the same on `Window`: upload tiles and CGB palettes without map changes.
- `Background.CopyRegion(TileMap map, byte destinationX, byte destinationY,
  byte width, byte height, byte sourceX, byte sourceY, byte firstTile,
  byte firstPalette)` and the same on `Window`: copy only map cells and CGB
  attributes. No tiles or palette memory are uploaded.
- `Hardware.Sprites.Load(SpriteAsset sheet, byte firstTile, byte firstPalette)`.
  The game sets sprite tile IDs and CGB palettes using these offsets itself.
- `Metasprites.Load(MetaspriteAsset sheet, byte firstTile, byte firstPalette)`.
  `Move`, `MoveFlippedX`, `MoveFlippedY`, and `MoveFlippedXy` each add an overload
  with `byte firstPalette` appended to the existing signature. `baseTile` already
  supplies tile placement; it is applied exactly once.

All tile IDs remain local in ROM, so independently converted assets can be
reused at different placements. Maps remap one row at a time using a 32-byte
automatic buffer. Attribute remapping adds the palette offset to bits 0–2 and
retains bits 3–7, including flips, VRAM bank and priority. Metasprites use GBDK's
existing geometry/flip implementation, then add the palette offset to the
resulting OAM palette bits. No persistent placement or camera state is kept.

Tile sums must be at most 256; CGB palette sums at most eight, with first palette
0–7. Validation uses wide sums before any narrowing. Background/window share tile
and palette storage; their tile IDs 128–255 also share physical storage with
sprite IDs 128–255 under GBDK's normal background addressing. Explicit graphics
uploads target VRAM bank 0. Attribute VRAM-bank flags are preserved, so assets
that deliberately use bank 1 need their corresponding tile data loaded there by
the caller. Upload APIs do not allocate VRAM. Sprite placements must start at an
even tile in hardware 8×16 mode; converted `TallSprites` assets also receive
constant alignment diagnostics. DMG keeps its existing shade registers and OAM
palette bit; CGB palette uploads and remapping are ignored there.

Source rectangles must fit the converted map; each dimension is at most 32.
Destination coordinates wrap modulo 32 in each axis. Zero-sized copies do
nothing. Invalid dynamic placements, sources, frames, or OAM ranges do nothing
(metasprite drawing returns zero); they never partially upload or wrap placement
indices. Compiler diagnostic GBS0629 rejects invalid constant cases, including a
selected frame's actual OAM extent. It requires converted asset data and is
intentionally absent from IDE analyzer diagnostics. Runtime-selected placements
still require the caller to reserve nonoverlapping resources.

All explicit paths preserve the caller's ROM bank; map copies and graphics
uploads also preserve CGB VBK. Upload during initialization with the display off;
schedule small visible map changes during safe VRAM update time. There is no
hidden scheduling, allocator, or overlap detection.
