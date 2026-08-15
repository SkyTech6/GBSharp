# Backgrounds and tilemaps

You will put artwork on the background layer three ways: a PNG on Game Boy Color, the same pipeline on an original Game Boy, and tile data written by hand, by reading `Samples/Background`, `Samples/BackgroundDmg` and `Samples/Tilemap` in the repo.

The background is what a Game Boy game is mostly made of. The hardware keeps a 32x32 grid of tile indices, of which 20x18 is on screen at a time; tile pixel data is loaded once into VRAM, and the map then names tiles by index, so a screen of artwork costs one byte per cell rather than one byte per pixel. Everything in this tutorial is a way of filling that grid.

## A PNG on Game Boy Color

`Samples/Background` targets the Game Boy Color; its `gbsharp.json` is one line:

```json
{ "name": "Background", "target": "gbc" }
```

The artwork enters the program as a named field:

```csharp
[Asset("forest.png")]
private static TileMap Forest;
```

Nothing here converts anything at runtime. While the project builds, `forest.png` is decoded, checked against the hardware's limits, reduced to 2bpp tiles, deduplicated, turned into a map, and (on Game Boy Color) split into palettes with a matching attribute map. The field is a name for that data in ROM; there is no conversion at runtime and no tool to run first. Anything wrong with the image is a compile error against your C#, pointing at this declaration.

Getting it on screen is one call, bracketed by the display:

```csharp
Display.Disable();

// Tiles, map, colour palettes and the attribute map, in one call. The
// sizes come from the image, so there is nothing here to keep in sync.
Background.Load(Forest);

Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);

Display.Enable();
Display.ShowBackground();
```

The `Disable`/`Enable` pair is a hardware rule, not a convention: VRAM is only safely writable with the LCD off, or during VBlank. A load this size will not fit in one VBlank, so the sample turns the screen off, copies everything, and turns it back on.

`Background.Load(Forest)` is one C# argument and eight C ones: tiles, map, attributes, palettes, and the counts, all filled in by the compiler from the image itself, plus the ROM bank the data lives in. The colour parts are skipped at runtime on an original Game Boy, which is what lets the next sample reuse the same call.

The loop scrolls with the d-pad:

```csharp
while (true)
{
    if (Input.Right) { scroll++; }
    if (Input.Left) { scroll--; }

    Background.Move(scroll, 0);
    Game.WaitVBlank();
}
```

`Background.Move` writes the scroll registers to an absolute position. The map is 32 tiles wide and the screen shows 20, so scrolling past the edge wraps around to the other side of the same map: the hardware wraps, there is no larger world behind it. `scroll` is a `byte` and the horizontal scroll register is a byte, so the overflow arithmetic and the hardware agree by construction.

## The same image on an original Game Boy

`Samples/BackgroundDmg` runs the same pipeline against `"target": "gb"`. The program is nearly identical; the differences are all in what the converter produces. From the sample's own header:

```csharp
// cave.png is drawn in four greys, which is all a DMG can show. The converter
// orders them lightest first to match the hardware's default palette, so the
// image is right before SetBackgroundShades is ever called, and rearranging
// that call is how you invert the picture without touching the artwork.
//
// No attribute map and no colour tables are generated for this target, so the
// same image costs less ROM here than it would on Game Boy Color.
```

On DMG the background has one palette of four shades, set as a register:

```csharp
Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);
```

Reorder those four arguments and the whole picture remaps instantly: that register-level remapping is how original hardware did fades and flashes. On Game Boy Color the call is ignored and the image's own palettes, generated at build time, apply instead.

## Tiles written by hand

`Samples/Tilemap` builds its background from data in the source, which is worth reading once even if you never do it again: it is what the asset pipeline is generating for you.

```csharp
// Four 8x8 tiles, 2 bits per pixel, 16 bytes each. Each row is two bytes:
// the low bit of all eight pixels, then the high bit.
private static readonly byte[] TileData =
{
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0: empty
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 1: solid
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,

    0xAA, 0x00, 0x55, 0x00, 0xAA, 0x00, 0x55, 0x00,  // 2: dither
    0xAA, 0x00, 0x55, 0x00, 0xAA, 0x00, 0x55, 0x00,

    0xFF, 0x00, 0x81, 0x00, 0x81, 0x00, 0x81, 0x00,  // 3: box
    0x81, 0x00, 0x81, 0x00, 0x81, 0x00, 0xFF, 0x00,
};
```

That layout (two bytes per row, low bitplane then high) is the hardware's native 2bpp format, exactly what a PNG becomes at build time. The `static readonly` matters as much as the bytes: it is what places the array in ROM. Drop the `readonly` and the build report moves it into the 8 KB of work RAM and charges you for it. Build with `--emit-c` and the array is there as a `const uint8_t` table.

The map names those tiles by index:

```csharp
// 10x9 of the 32x32 map. The rest stays tile 0.
private static readonly byte[] Map =
{
    3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    3, 0, 0, 2, 2, 2, 2, 0, 0, 3,
    3, 0, 1, 1, 0, 0, 1, 1, 0, 3,
    3, 2, 1, 0, 0, 0, 0, 1, 2, 3,
    3, 2, 0, 0, 1, 1, 0, 0, 2, 3,
    3, 2, 1, 0, 0, 0, 0, 1, 2, 3,
    3, 0, 1, 1, 0, 0, 1, 1, 0, 3,
    3, 0, 0, 2, 2, 2, 2, 0, 0, 3,
    3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
};
```

Loading is the two halves of what `Background.Load` did in one:

```csharp
Background.LoadTiles(0, 4, TileData);
Background.LoadMap(0, 0, 10, 9, Map);
```

`LoadTiles` copies pixel data into VRAM, 16 bytes a tile; `LoadMap` writes a rectangle of indices into the 32x32 grid. The sample only fills a 10x9 rectangle, and the rest of the grid stays tile 0, the empty tile, which is why it deliberately defines one.

The sample targets `gbc` but runs on both machines, and handles colour the honest way: by asking:

```csharp
// Set the DMG shades either way: on colour hardware they are ignored,
// and on an original Game Boy they are all there is.
Palettes.SetBackgroundShades(Shade.White, Shade.LightGray, Shade.DarkGray, Shade.Black);

if (Palettes.IsColorHardware)
{
    Palettes.LoadBackgroundColors(0, 1, Colors);
}
```

`Colors` is one Game Boy Color palette, four 15-bit colours as `ushort`s in ROM:

```csharp
private static readonly ushort[] Colors = { 0x7FFF, 0x35AD, 0x1A73, 0x0000 };
```

The rest of the sample scrolls on both axes with `Background.Move(scrollX, scrollY)` and plays a tone on the edge of the A button (press, not hold), which is worth reading as the standard pattern for turning a level into an event.

## Maps larger than one screen

The hardware map is 32x32 cells; a world larger than that does not fit in it. None of these three samples needs more, but the primitive for when you do is `Background.DrawRegion`, which copies a window of a larger converted map into the hardware map; you track the camera position yourself and call it when the position moves. GB# does not keep a camera for you: that would be per-frame state you cannot see the cost of. The [assets guide](../guides/assets.md) covers large maps in full.

## Run it

```
gbsharp run Samples/Background
```

A forest scene fills the screen in colour; hold Left or Right and it scrolls, wrapping at the map's edge. Then try the other two:

```
gbsharp run Samples/BackgroundDmg
gbsharp run Samples/Tilemap
```

`BackgroundDmg` is the same program in four greys. `Tilemap` shows the hand-built pattern in its 10x9 rectangle, scrolls on both axes, and beeps when you press A. Build any of them with `--emit-c` to see the data tables, and read the build report to see what each image cost: the DMG image is measurably cheaper, because no attribute map and no colour tables were generated for it.

## Where to go next

- [Drawing text](drawing-text.md): a font is background tiles too.
- [The asset pipeline](../guides/assets.md): every attribute, limit and diagnostic.
- [Memory and budgets](../guides/memory-and-budgets.md): where `static readonly` goes, and how to hold the line.
