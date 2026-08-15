# Assets

Drop a PNG next to your code and name it:

```csharp
[Asset("forest.png")]
private static TileMap Forest;

public static void Main()
{
    Background.Load(Forest);
}
```

While the project builds, the image is decoded, checked against the hardware's limits, reduced to 2bpp tiles, deduplicated, turned into a map, and (on Game Boy Color) split into palettes with a matching attribute map. The field is a name for that data; there is no conversion at runtime and no tool to run first.

`Background.Load(Forest)` is one C# argument and eight C ones, with the sizes filled in from the image, so there is nothing to keep in sync:

```c
gbs_background_load(Program_Forest_tiles, Program_Forest_map,
                    Program_Forest_attributes, Program_Forest_palettes,
                    9U, 20U, 18U, 3U, 0U);
```

The last argument is the ROM bank the data lives in; `0` means it is always mapped and no bank switch is needed. Assets placed in other banks travel with their bank: see [Banking](banking.md).

## The attributes

Each attribute converts a file at build time and gives the result a shape the loaders understand. Paths resolve relative to the file that declares them, then to the project's `Assets` folder, then to the project root.

**`[Asset]`** is background artwork. The field is a `TileMap` (tiles plus a map) or a `TileSet` (tiles only, for code that builds its own maps). Tiles are deduplicated; on Game Boy Color, mirrored tiles can share one copy too (`DedupeFlips`, on by default there), because the attribute map carries flip bits. An original Game Boy's map has one byte per cell and no room for them, so `DedupeFlips` there is an error rather than a silently wrong image ([GBS0614](../reference/diagnostics/assets.md#gbs0614-flip-deduplication-unavailable)). The [Backgrounds and tilemaps](../tutorials/backgrounds-and-tilemaps.md) tutorial starts here.

**`[Sprite]`** is a sprite sheet, sliced into 8x8 tiles row-major. Flipped duplicates always share one copy, because OAM carries flip bits. Set `TallSprites` for hardware 8x16 mode: the pairing that mode requires (each sprite's top and bottom tiles adjacent and even-aligned) is not what a row-major slice produces, so it is an explicit property rather than a guess.

**`[Metasprite]`** is an animated character made of several sub-sprites. The sheet is a grid of frames, `FrameWidth` by `FrameHeight` tiles each; a frame's sub-sprites are whichever of its tiles are not entirely transparent, so a frame spends no hardware sprite, no ROM, and no OAM write on empty space. It is a different declaration from `[Sprite]`, not an option on it, because the converted data has a different shape: a per-frame list of placements, not just a tile array. See the [Metasprites](../tutorials/metasprites.md) tutorial.

**`[Font]`** is one row of 8x8 glyphs plus a `Characters` string naming them in sheet order. Drawing text is covered in [Drawing text](text.md).

**`[Binary]`** is a file copied into ROM unchanged, for data GB# has no opinion about: level layouts, a table another tool produced. See [Data in ROM](rom-data.md).

## What every build tells you

Assets cost ROM, and the build says exactly what each one cost, every time:

```
Assets
  Forest          forest.png      20x18   360 -> 9 tiles, 3 palettes          888 B
```

The same figure is reported as a diagnostic against the declaration ([GBS0620](../reference/diagnostics/assets.md#gbs0620-asset-rom-cost)), so the cost lives where the field does:

```
Program.cs(6,28):  resource GBS0620: Program.Forest places 888 bytes in ROM: 360 tiles (9 unique), 20x18 map.
```

## Image problems are C# compile errors

Anything wrong with the image is a compile error against **your C#**, not against the image:

```
Program.cs(6,28): error GBS0601: 'player.png' contains 6 colours. A 2bpp palette holds 4.
        private static SpriteAsset Player;
                                   ^^^^^^
    Reduce the image to 4 colours, or target Game Boy Color, where each 8x8 tile
    can use its own 4-colour palette out of 8.
```

The decoder is part of GB# rather than a dependency, which is what lets every rejection be a diagnostic that names the fix: too many colours ([GBS0601](../reference/diagnostics/assets.md#gbs0601-too-many-colours)), dimensions off the 8-pixel grid ([GBS0605](../reference/diagnostics/assets.md#gbs0605-dimensions-not-tile-aligned)), a file that is not where the path says ([GBS0606](../reference/diagnostics/assets.md#gbs0606-asset-not-found)), and the rest of the [asset diagnostics](../reference/diagnostics/assets.md).

One consequence of the field being a name for ROM data: an asset is not a value. It can be passed to the loader that understands it (`Background.Load`, `Text.Load`) but not copied or stored ([GBS0613](../reference/diagnostics/assets.md#gbs0613-asset-used-as-a-value)).

## DMG and GBC palettes

The two machines want different things from an image, and the pipeline handles both.

On the **original Game Boy** (DMG), a tile is four shades. An image with at most four colours converts directly, brightest to darkest. Colours beyond that are refused ([GBS0601](../reference/diagnostics/assets.md#gbs0601-too-many-colours)); colours the hardware cannot show are converted by brightness, with a warning that names the alternative ([GBS0612](../reference/diagnostics/assets.md#gbs0612-colours-on-an-original-game-boy)). Set `"target": "gbc"` in [gbsharp.json](../reference/gbsharp-json.md) to keep them.

On **Game Boy Color**, every 8x8 tile draws from one of 8 four-colour background palettes. The pipeline splits the image's colours into palettes and emits an attribute map assigning one to each tile: that is the `attributes` and `palettes` pair in the generated call above. A single tile using more than four colours cannot be split and is an error at that tile's coordinates ([GBS0602](../reference/diagnostics/assets.md#gbs0602-tile-uses-too-many-colours)); an image needing more than 8 palettes is refused with the observation that colours appearing in the same tile must live in the same palette, so moving one colour can free a whole palette ([GBS0603](../reference/diagnostics/assets.md#gbs0603-too-many-palettes)).

`Samples/Background` and `Samples/BackgroundDmg` are the same PNG built for both machines.
