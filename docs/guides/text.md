# Drawing text

Text on a Game Boy is tiles. A font is a tileset where each tile happens to be a glyph, and drawing a string is writing those tiles into the background map, the same map write everything else on the layer uses. GB# keeps that model visible instead of wrapping it: there is no cursor, no scrolling, and no `printf`.

## A font is an asset

`[Font]` converts a font sheet into background tiles and a character-to-tile lookup at build time:

```csharp
[Font("font.png", Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,!?")]
private static FontAsset Alphabet;
```

The sheet is one row of 8x8 glyphs, one tile per character in `Characters`, left to right, so the image must be exactly `Characters.Length` tiles wide and one tile tall ([GBS0627](../reference/diagnostics/assets.md#gbs0627-font-sheet-has-the-wrong-shape)), and `Characters` is required because there is no default set to fall back to ([GBS0628](../reference/diagnostics/assets.md#gbs0628-font-characters-required)). Monospaced, single-row fonts are a deliberate v1 simplification, not a missing feature: proportional glyph widths need a per-glyph advance table and a text layout GB# does not have yet, and monospaced text is what most Game Boy games draw anyway.

A font carries no colour of its own. A background cell already has whatever palette or attribute is active there, so drawing text never touches either.

## The Text API

Three methods, and nothing hidden between them:

- **`Text.Load(font, firstTile)`** uploads the font's glyph tiles into the background/window tile region, starting at `firstTile`. Once, typically at startup.
- **`Text.Draw(font, firstTile, x, y, length, text)`** draws `length` bytes of `text` as tiles, left to right starting at `(x, y)` on the background. Each byte is a character code, looked up in the font's glyph table and written as a background tile: the same map write `Background.SetTile` makes, just several in a row.
- **`Text.DrawWindow(...)`** is the same call onto the window layer instead of the background.

A code the font's `Characters` did not declare draws whichever glyph is tile 0, rather than being checked at runtime, because a bounds check on every glyph of every draw would be a cost paid by correct code to catch a mistake the build could not see anyway.

## No cursor, no scrolling: by design

GBDK's own `gbdk/font.h` and `console.h` exist for the opposite choice: a cursor and scrolling state the game does not control. That is exactly the kind of hardware-hiding layer GB# does not add, since GB# abstracts boilerplate, not hardware. Where text goes is a decision the game makes with coordinates, every time, and redrawing a region is writing over it, which is what `Samples/Text` does with its counter:

```csharp
[Font("font.png", Characters = "0123456789HI")]
private static FontAsset Digits;

// "HI", as the character codes gbs_font_draw indexes the glyph table with.
private static readonly byte[] Greeting = { 72, 73 };

private static byte[] counter = { 48, 48 }; // "00"

public static void Main()
{
    Display.Disable();
    Text.Load(Digits, 0);
    Display.Enable();
    Display.ShowBackground();

    Text.Draw(Digits, 0, 1, 1, 2, Greeting);
    Text.Draw(Digits, 0, 1, 3, 2, counter);
    // …update counter's bytes, then Text.Draw the same region again.
}
```

## Strings are byte arrays

GB#'s subset has no `System.String` ([GBS0043](../reference/diagnostics/language.md#gbs0043-systemstring-is-unavailable)): it would need heap allocation the target does not have, so there is no string literal to pass to `Draw` yet. Write the bytes directly, as `Samples/Text` does above: each byte is a character code, meaning an index into the font's `Characters`. Making them `static readonly` puts them in ROM rather than work RAM ([Data in ROM](rom-data.md)).

Sugar that turns a string literal into that array at compile time is real, separate work (a language-subset change with its own review), and is deliberately not part of this.

The [Drawing text](../tutorials/drawing-text.md) tutorial builds the counter above from scratch.
