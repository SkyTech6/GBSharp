# Banking a big game

You will build a cartridge larger than 32 KB and control where every piece of it goes, by reading `Samples/Banking` in the repo: a working 64 KB MBC5 cartridge using both `[Bank]` forms, explicit and automatic, plus banked assets.

The Game Boy addresses 32 KB of cartridge: 16 KB permanently mapped at the bottom (bank 0), and 16 KB at a time in a switchable window above it. A game without banking stops at 32 KB. A memory bank controller (the MBC chip on the cartridge) swaps which 16 KB bank sits in that window, and banking is the discipline of deciding what lives where and paying for the switches.

## The cartridge is configuration

`Samples/Banking`'s `gbsharp.json`:

```json
{
  "name": "Banking",
  "target": "gbc",
  "mbc": "mbc5+ram+battery",
  "romBanks": 4
}
```

`"mbc"` names the controller chip and its extras: MBC5 with cartridge RAM and a battery to keep it alive when the power is off. `"romBanks": 4` is four 16 KB banks: a 64 KB ROM. Left unset, a banked project gets MBC5 with battery-backed RAM and a ROM sized to fit; this sample pins both so the numbers in this tutorial stay put.

## What stays in bank 0

From the sample's own header:

```csharp
// Bank 0 is always mapped and is where everything starts. It holds the frame
// loop and anything that runs every frame, because reaching a banked function
// costs a bank switch each way. Everything else is worth moving out: bank 0 is
// the one space no larger cartridge can give you more of.
```

`Program` carries no `[Bank]` attribute, so it stays in bank 0:

```csharp
public static class Program
{
    // Not banked. Read every frame, so it stays where it is already mapped.
    private static byte scroll;

    public static void Main()
    {
        Display.Enable();

        // A banked call. GB# reports the cost of this line at build time.
        ForestLevel.Load();

        while (true)
        {
            scroll++;
            Background.Scroll(1, 0);

            Game.WaitVBlank();
        }
    }
}
```

The frame loop runs sixty times a second and calls nothing banked, which is the shape to aim for. The one banked call happens once, at setup, and the build prices it anyway (GBS0301): a banked call goes through a trampoline that saves the current bank, switches, calls, and switches back, roughly thirty cycles more than a local call. Thirty cycles once at startup is nothing; thirty cycles inside the frame loop is a tax on every frame, and a separate diagnostic (GBS0440) exists precisely to catch banked calls reached from the frame loop.

## An explicit bank

```csharp
[Bank(2)]
public static class ForestLevel
{
    [Asset("forest.png")]
    private static TileMap Art;

    public static void Load() => Background.Load(Art);
}
```

One attribute moves the whole class out of the permanent half: the code, the tiles, the map, the attributes and the palettes all go to bank 2 together. The generated C says `#pragma bank 2` on its first line, and the build report lists what landed there.

The asset follows its declaring class, and `Background.Load` is handed the bank alongside the pointers, so it maps the data in before reading and restores the previous bank afterwards; writing that switch-read-restore sequence yourself is what `[Bank]` is instead of. It also restores the bank for a reason a single call site cannot see: without that, loading banked art would silently change which bank the caller returns into.

Keeping data with the code that loads it is the pattern to copy. Data outside bank 0 can only be read while its bank is mapped, so reading `ForestLevel.Art` from a class in another bank is a compile error (GBS0303) rather than a silently wrong load.

## An automatic bank

```csharp
[Bank]
public static class Credits
{
    private static readonly byte[] Text =
    {
        0x47, 0x42, 0x23, 0x20, 0x53, 0x41, 0x4D, 0x50, 0x4C, 0x45,
    };

    public static byte Initial() => Text[0];
}
```

Written without a number, `[Bank]` says "not bank 0, anywhere else" and lets the linker choose. The build then tells you what it chose:

```
Program.cs(7,24): info GBS0309: 'Credits.Initial()' was placed automatically in bank 1.
    GB# left this to GBDK's bankpack rather than choosing itself. Write [Bank(n)] on the
    declaration, with the bank named above, to pin it there instead.
```

That is the intended workflow: let placement float while the game is growing, then pin banks once the layout settles, so a later change cannot silently reshuffle what a save file or a level table depends on.

## Reading the placement report

Every build ends with the layout. For a cartridge shaped like this one it reads:

```
Cartridge                 MBC 0x1B, 4 banks

ROM Banks
  Bank 0                  754 B / 16.0 KB  ░░░░░░░░░░░░░░░░░░░░
  Bank 1                  14 B / 16.0 KB  ░░░░░░░░░░░░░░░░░░░░
  Bank 2                  918 B / 16.0 KB  █░░░░░░░░░░░░░░░░░░░  (888 B declared)

Placement
  Bank 2                  ForestLevel.Load(), ForestLevel_Art_map, ForestLevel_Art_tiles, …
  Bank (chosen)           Credits.Initial(), Credits_Text
```

`MBC 0x1B` is the cartridge-header byte for MBC5 with RAM and battery, the `"mbc"` string, as the hardware spells it. Each bank shows what the linker actually placed against its 16 KB, with the declared figure beside it where they differ: the difference is the code in that bank, and reporting one number would be a useful-sounding lie. The Placement section is the answer to "where did it go": explicit placements listed under their bank, automatic ones under `Bank (chosen)` with the bank the linker picked, which is the number to copy into `[Bank(n)]` when you pin.

One failure mode deserves its own mention: bank 0 filling up is a note, but bank 0 *overflowing* is a GB# error (GBS0310), because the linker does not treat it as one: spilled code lands where the switchable bank appears, the ROM builds, and then dies when it reaches the part that moved. GB# reads the real linker map and refuses instead.

## Run it

```
gbsharp run Samples/Banking
```

You should see the forest artwork (loaded out of bank 2 by a banked call) scrolling steadily leftward one pixel per frame. The screen is the least of it: run `gbsharp build Samples/Banking` and read the report. Find the GBS0309 line naming the bank `Credits` was given, find the GBS0301 line pricing the `ForestLevel.Load()` call, and match the Placement section against the three classes in the source.

## Where to go next

- [Banking in full](../guides/banking.md): trampolines, diagnostics and layout advice from the call graph.
- [Memory and budgets](../guides/memory-and-budgets.md): budgets that fail the build when a bank fills.
- [The CLI](../reference/cli.md): `build`, `--report-json` and reading reports in CI.
