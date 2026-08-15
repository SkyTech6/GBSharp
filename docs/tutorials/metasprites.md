# Metasprites

You will draw a character bigger than one hardware sprite and animate it, by reading `Samples/Metasprite` in the repo.

A hardware sprite is 8x8 pixels, and there are 40 of them. Anything that reads as a character is several of them moved together: a metasprite. GB#'s `Metasprites` class is a thin wrapper over GBDK's own `move_metasprite_ex` family: which hardware sprites and which tiles a frame uses are arguments on every call, never implicit state kept for you.

## The sheet

```csharp
[Metasprite("hero.png", FrameWidth = 2, FrameHeight = 2)]
private static MetaspriteAsset Hero;
```

`hero.png` is a grid of frames, each `FrameWidth` by `FrameHeight` tiles: here 2x2 tiles, 16x16 pixels. The sheet is 32x16, so it holds two frames, read left to right. At build time each frame becomes a list of sub-sprite placements plus the deduplicated tiles they use.

The interesting part is what a frame does not contain. From the sample's own header:

```csharp
// A 32x16 sheet: two 2x2-tile frames, each missing one sub-sprite - blank,
// palette index 0, the colour real hardware never draws for a sprite. That
// sub-sprite costs no OAM entry and no frame-table byte; --emit-c and read
// Program_Hero_frames to see it: three metasprite_t records per frame, not
// four, each ended by GBDK's own terminator.
```

Palette index 0 is transparent for sprites on real hardware, so a tile that is entirely index 0 can never be seen. The converter drops it: no ROM for the placement record, no hardware sprite spent on it, no OAM write at runtime. A 2x2 frame with a blank corner is three sub-sprites, not four. This is why frames of the same metasprite can use different numbers of hardware sprites, and that fact drives the one piece of bookkeeping this sample has to do.

## Load, then move

```csharp
Display.Enable();
Display.ShowSprites();

Metasprites.Load(Hero);
```

`Display.ShowSprites()` flips the LCD controller bit that makes the sprite layer visible at all: the background and sprites are independently switchable layers. `Metasprites.Load` uploads the sheet's tiles and, on Game Boy Color, its palettes. Once, before the loop; the per-frame work is only OAM writes.

```csharp
byte used = Metasprites.Move(Hero, frame, 0, 0, x, 80);
```

`Move(sheet, frame, baseTile, baseSprite, x, y)` positions one frame at an absolute screen position by writing OAM entries for each of its sub-sprites, starting at hardware sprite `baseSprite` and drawing tiles relative to `baseTile`. It returns how many hardware sprites the frame used (three for these frames), and that return value is not decoration.

## Hiding what the last frame drew

```csharp
// Frames can use different numbers of sub-sprites; hide whatever
// the last frame drew that this one did not reuse.
if (used < usedLastFrame)
{
    Metasprites.HideRange(used, usedLastFrame);
}

usedLastFrame = used;
```

`Move` writes as many OAM entries as this frame needs and does not touch the rest. If the previous frame used four sprites and this one uses three, the fourth entry still holds whatever the previous frame put there, and it stays on screen: a stale limb hanging in the air. `HideRange(from, to)` moves hardware sprites `from` up to (not including) `to` off screen, so hiding from this frame's count up to the last frame's count cleans up exactly the leftovers and nothing else. The hardware forgets nothing on its own; a frame boundary is a concept the game keeps, not one OAM has.

## Animation

```csharp
if (Input.Right) { x++; }
if (Input.Left) { x--; }
```

```csharp
Game.WaitVBlank();

frame = frame == 0 ? (byte)1 : (byte)0;
```

The sheet's two frames put their blank corner in different places (frame 0's is bottom-right, frame 1's is top-right), so alternating between them every frame reads as a two-frame step animation. Animation on this hardware is exactly this: choosing a different frame index on the next `Move` call. There is no animation system underneath, because a frame index and a toggle is the whole mechanism, and anything wrapped around it would be state you could not see the cost of.

## Run it

```
gbsharp run Samples/Metasprite
```

You should see a 16x16 character mid-screen, stepping in place, and it walks left and right with the d-pad. Build with `--emit-c` and read `Program_Hero_frames` to see the per-frame placement records: three per frame, each list ended by GBDK's own terminator.

## Where to go next

- [Many objects](many-objects.md): eight of these, updated data-oriented.
- [The asset pipeline](../guides/assets.md): sheets, deduplication and limits.
- [Profiling and cost](../guides/profiling-and-cost.md): what a frame of OAM writes costs.
