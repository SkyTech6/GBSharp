# Composing a scrolling scene

`Samples/ComposedScrolling` explores a 32 by 32 tile world with the D-pad.
The window holds a fixed bottom status bar, with two independently converted
metasprites above it. The world lives in ROM bank 2. Generate its fixture art
with `python tools/generate-composed-art.py`; the font comes from `Samples/Text`.

## Allocate before uploading

The sample uses GBDK's default signed background tile addressing. Background
and window share tile storage and CGB background palettes. Sprite tiles use
unsigned addressing, overlapping part of that storage. These are physical
allocations, not separate pools per asset:

| Asset | Tile indices | Physical VRAM | CGB palettes | OAM |
|---|---|---|---|---|
| World | BG 0–63 | 0x9000–0x93ff | BG 0 | — |
| HUD font | BG 128–139 | 0x8800–0x88bf | BG 1 | — |
| Hero | OBJ 192–195 | 0x8c00–0x8c3f | OBJ 0 | 0–3 |
| Enemy | OBJ 196–199 | 0x8c40–0x8c7f | OBJ 1 | 4–7 |

Changing LCD tile addressing changes the physical background ranges. In the
default signed mode BG 0–127 occupy 0x9000–0x97ff and BG 128–255 occupy
0x8800–0x8fff. Sprite 0–255 occupy 0x8000–0x8fff. A different allocation must
account for those overlaps. CGB palette indices range from 0 to 7 separately
for backgrounds and objects. Four colours occupy each palette. The font API
uploads tiles only; set its palette and window attributes explicitly.

## Upload once, copy the exposed cells

At startup, with the LCD disabled:

```csharp
Background.LoadGraphics(Map, 0, 0);
Background.CopyRegion(Map, 0, 0, 20, 18, 0, 0, 0, 0);
Text.Load(Font, 128);
Metasprites.Load(Hero, 192, 0);
Metasprites.Load(Enemy, 196, 1);
Metasprites.Move(Hero, 0, 192, 0, 80, 80, 0);
Metasprites.Move(Enemy, 0, 196, 4, 128, 96, 1);
```

`CopyRegion` arguments are asset, destination X/Y, width/height, source X/Y,
first tile and first palette. On a rightward tile step, increment the camera
and copy the new right column:

```csharp
Background.CopyRegion(Map, right, top, 1, 18, right, top, 0, 0);
```

The hardware map is a 32 by 32 ring. Destination coordinates wrap modulo 32;
source coordinates must fit the converted asset, and dimensions cannot exceed
32. The sample limits its camera to 12 tiles horizontally and 14 vertically,
and updates one exposed strip every eight frames. It retains all camera and
scheduling state in game code. `World.Column` and `World.Row` access banked
assets; the runtime maps their ROM data and restores the caller's ROM bank.

`Window` offers the same `LoadGraphics`, `CopyRegion` and explicit `Load`
overloads. `Hardware.Sprites.Load(sheet, firstTile, firstPalette)` places a
sprite sheet; set each SpriteRef's tile and colour-palette selection to match.
All four metasprite move variants accept a final palette offset. Their tile
offset is the existing `baseTile` argument; supply it once. Palette remapping
preserves flip, priority, and other non-palette attribute bits.

## Bounds and timing

Tile placement must fit 256 tiles; palette placement must fit eight CGB
palettes. Each converted asset supports at most 255 unique tiles; placements
may end at tile 255. An empty converted metasprite does not upload tile data.
In 8 by 16 sprite mode, use an even first tile; each hardware object consumes
a tile pair. Keep OAM ranges within 0–39. Provably invalid constant placements
produce compiler diagnostic `GBS0629`. Dynamic invalid placements and invalid
copy rectangles do nothing, without wrapping placement arithmetic. The IDE
does not convert image files, so this asset-dependent diagnostic appears at
GB# compile time. Overlap between runtime-selected allocations remains the
caller's responsibility.

DMG ignores CGB palette placement and attribute maps. Configure its background
and object shade registers separately. DMG still uses the same tile memory
and addressing constraints. The acceptance tests build both DMG and CGB ROMs.

Initialize large uploads with the display disabled. During play, wait for
VBlank and bound the update workload. GBDK's VRAM routines wait for accessible
VRAM, but large work can extend beyond a frame and cause visible partial
updates. A row or column update is cheaper than a complete screen; measure
your asset sizes and target hardware before choosing a schedule.

Existing `Load(asset)` and `Background.DrawRegion(...)` retain their zero-based
upload behaviour. To migrate, allocate locations, call `LoadGraphics` once,
and replace repeated `DrawRegion` calls with `CopyRegion` using the same
placement offsets. Do not use the old upload path for assets that now coexist
at nonzero locations.

## Verification

Run `dotnet test GBSharp.Tests -c Release --filter FullyQualifiedName~ComposedScrollingTests`
with the pinned GBDK and emulator fetched. Tests exercise actual input, both
scroll axes, unchanged character graphics/OAM, and an unchanged HUD on DMG/CGB.
The profiler test measures twenty exposed 1 by 18 columns from the same world
using `DrawRegion` and `CopyRegion`, including their runtime callees. The LCD
is disabled during measurement to isolate update work from scanline waits;
these numbers are not a VBlank budget guarantee. With GBDK 4.5.0 and emulator
0.6.0, the 64-tile world measured 1,743,220 ticks for `DrawRegion` and
1,237,140 ticks for `CopyRegion`: 29.0% less update work. The row-remapping
loop still has a cost; savings depend on tileset size and rectangle shape.

For installed-tool and browser checks, see
[browser delivery validation](../../tools/browser-delivery/README.md). The
release workflow tests the exact four packed packages before publishing them.
