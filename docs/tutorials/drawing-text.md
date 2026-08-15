# Drawing text

You will draw a static label and a counter that ticks once a second, from one font sheet, by reading `Samples/Text` in the repo.

Text on a Game Boy is not a console. There is no cursor, no scrolling, and no `printf`: a string is a row of tiles like any other background artwork. One call uploads the glyphs once, and another writes them into the map wherever the game wants them. GBDK's own `font.h` and `console.h` exist for the opposite choice (a cursor and scrolling state the game does not control), which is exactly the kind of hardware-hiding layer GB# does not add. GB# abstracts boilerplate, not hardware.

## A font is an asset

```csharp
[Font("font.png", Characters = "0123456789HI")]
private static FontAsset Digits;
```

`font.png` is one row of 8x8 glyphs, one tile per character in `Characters`, left to right, so this image is exactly twelve tiles wide and one tile tall. At build time the sheet becomes background tiles and a character-to-tile lookup; like every asset, the field is a name for data in ROM, and anything wrong with the image is a compile error pointing at this declaration.

`Characters` declares which character each glyph is, in sheet order. This font carries only what the sample draws: ten digits and the two letters of its greeting. A font pays for exactly the glyphs it declares, in ROM and in VRAM, which is why there is no default character set to fall back to.

## Strings are byte arrays

```csharp
// "HI", as the character codes gbs_font_draw indexes the glyph table with.
private static readonly byte[] Greeting = { 72, 73 };

private static byte[] counter = { 48, 48 }; // "00"
```

GB#'s subset has no `string` (GBS0043): a string is heap-allocated, immutable, UTF-16 data, none of which exists on this machine. So text is bytes: 72 and 73 are 'H' and 'I', 48 is '0'. Each byte is a character code that `Text.Draw` looks up in the font's glyph table. Sugar that turns a string literal into an array like this at compile time is real, separate work (a language-subset change with its own review), and is deliberately not part of this.

The two declarations differ in one word, and that word is where the data lives. `Greeting` is `static readonly`, so it is placed in ROM and can never change. `counter` is mutable, so it lives in the 8 KB of work RAM, which it must, because the program rewrites it every second. The build report itemises both.

## Load once, draw where you like

```csharp
Display.Disable();
Text.Load(Digits, 0);
Display.Enable();
Display.ShowBackground();

Text.Draw(Digits, 0, 1, 1, 2, Greeting);
Text.Draw(Digits, 0, 1, 3, 2, counter);
```

`Text.Load` uploads the font's glyph tiles into the background tile region, starting at tile 0, done with the LCD off, because VRAM is only safely writable then or during VBlank. It happens once; the glyphs are just tiles from here on, shared with everything else the background draws.

`Text.Draw(font, firstTile, x, y, length, text)` writes `length` bytes of `text` as tiles, left to right from the map cell (x, y). It is the same map write `Background.SetTile` makes, just several in a row. The coordinates are tile coordinates on the 32x32 background map, and nothing advances on its own: drawing "HI" at (1, 1) leaves the hardware exactly as it was except for two map cells. That is the whole reason there is no cursor: a cursor is state, state costs WRAM and cycles, and this way the only text state that exists is what your game chose to keep.

## The counter

```csharp
while (true)
{
    Game.WaitVBlank();
    frames++;

    if (frames == 60)
    {
        frames = 0;
        value++;

        if (value == 100)
        {
            value = 0;
        }

        counter[0] = (byte)(48 + (value / 10));
        counter[1] = (byte)(48 + (value % 10));

        Text.Draw(Digits, 0, 1, 3, 2, counter);
    }
}
```

`Game.WaitVBlank` returns once per frame at 59.7 Hz, so sixty of them is close enough to a second. The counter is formatted by hand (tens digit, ones digit, each offset from 48 ('0')) and redrawn only on the seconds it changes. The other fifty-nine frames, the loop draws nothing: the two map cells still hold their tiles, and the hardware keeps showing them. Text you have not touched costs nothing per frame, which is the property a console-style text layer would have taken away.

## Run it

```
gbsharp run Samples/Text
```

You should see "HI" near the top-left, a two-digit counter below it counting up once a second, and it rolling over from 99 to 00. Build with `--emit-c` to see `Greeting` as a `const` table in ROM and `counter` as two bytes of WRAM.

## Where to go next

- [Text in full](../guides/text.md): the window layer, fonts and their limits.
- [Backgrounds and tilemaps](backgrounds-and-tilemaps.md): the layer text is drawn on.
- [API reference](../api/index.md): `Text`, `FontAttribute` and `FontAsset`.
