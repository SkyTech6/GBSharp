# Banking

A Game Boy maps 16 KB of cartridge permanently and 16 KB at a time, so a game without banking stops at 32 KB. The permanent half is bank 0: it holds the interrupt vectors, the GBDK runtime, and everything you do not mark otherwise. The other window shows one switchable bank at a time, chosen by writing to the cartridge's mapper. Banking is the discipline of deciding what lives where and paying for the switches, and `[Bank]` is that discipline as one attribute.

## `[Bank(n)]` on a class

```csharp
[Bank(2)]
public static class ForestLevel
{
    [Asset("forest.png")]
    private static TileMap Art;

    public static void Load() => Background.Load(Art);
}
```

The code, the tiles, the map, the attributes and the palettes all go to bank 2 together. `Background.Load` is handed the bank alongside the pointers, so it maps the data in before reading and puts the previous bank back afterwards: writing that yourself is what `[Bank]` is instead of.

A type's attribute applies to its methods and its `static readonly` fields, and a member's own attribute beats its containing type's, so `[Bank(0)]` on one method of a banked class forces that one member to stay resident. Mutable statics cannot be banked ([GBS0306](../reference/diagnostics/banking.md#gbs0306-mutable-data-cannot-be-banked)): they live in work RAM, which is always mapped and is not banked on this hardware. And `Main` must stay in bank 0 ([GBS0300](../reference/diagnostics/banking.md#gbs0300-the-entry-point-cannot-be-banked)): execution starts there before any bank has been switched in.

## Automatic placement, then pinning

Written without a number, `[Bank]` lets the linker choose and then tells you what it chose, so you can pin it once the layout settles:

```
Program.cs(7,24): info GBS0309: 'Credits.Initial()' was placed automatically in bank 1.
    GB# left this to GBDK's bankpack rather than choosing itself. Write [Bank(n)] on the
    declaration, with the bank named above, to pin it there instead.
```

The intended workflow is exactly what [GBS0309](../reference/diagnostics/banking.md#gbs0309-automatic-placement) describes: start automatic while the game is growing, read what the build chose, and write the numbers down when you want the layout to stop moving.

## What a banked call costs

Reaching banked code is not free, and the build says where you are paying for it:

```
Program.cs(20,9): performance GBS0301: Calling 'ForestLevel.Load()' switches to ROM bank 2.
    A banked call goes through a trampoline that saves the current bank, switches, calls, and
    switches back, which costs roughly a hundred cycles more than a local call.
```

The caller's own bank is unmapped for the duration. [GBS0301](../reference/diagnostics/banking.md#gbs0301-banked-call) fires at every banked call site; the call-graph analysis adds two things a single site cannot know: that a call switches banks *on the path that runs sixty times a second* ([GBS0440](../reference/diagnostics/cycle-cost.md#gbs0440-banked-call-every-frame)), and that a callee's callers all sit in one other bank it could share ([GBS0441](../reference/diagnostics/cycle-cost.md#gbs0441-callee-could-share-its-callers-bank)). Neither fires on setup code, and neither will ever suggest moving banked code *into* bank 0: that would be advising you to undo the `[Bank]` you wrote, and bank 0 is the 16 KB banking exists to protect.

## Reading banked data

Data outside bank 0 can only be read while its bank is mapped, so reading it directly from elsewhere is an error rather than a silently wrong load:

```
Program.cs(14,22): error GBS0303: 'ForestLevel.Art' is in ROM bank 2 and cannot be read directly.
```

Pass the data to a loader that takes its bank, such as `Background.Load`, or switch explicitly with `Banking.Switch` and take responsibility for restoring the previous bank yourself ([GBS0303](../reference/diagnostics/banking.md#gbs0303-banked-data-read-directly)). Explicit switching is only safe from resident code: a banked function that switches banks unmaps itself.

## What the build shows you

The generated C says where it lands on its first line, and every build ends with the layout:

```c
#pragma bank 2

#include "game.h"

const uint8_t ForestLevel_Art_tiles[144] = { /* … */ };   /* 144 bytes, ROM bank 2 */
BANKREF(ForestLevel_Art_tiles)

void ForestLevel_Load(void) BANKED
```

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

Declared and actual are shown separately for the same reason they are for WRAM: the difference is the code in that bank, and reporting one number would be a useful-sounding lie. See [Memory and budgets](memory-and-budgets.md) for the same principle applied to work RAM.

## When bank 0 fills up

Bank 0 filling is a note ([GBS0307](../reference/diagnostics/banking.md#gbs0307-bank-0-nearly-full)); bank 0 *overflowing* is an error, because the linker does not treat it as one. Areas are placed in order and past `0x4000` they land where the switchable bank appears, so the ROM builds and then dies as soon as it reaches the part that moved. Neither lcc nor sdld mentions it, and the usage table above cannot show it: the spilled bytes are counted against the bank whose addresses they occupy, leaving bank 0 at a plausible-looking 97%:

```
error GBS0310: Bank 0 overflowed by 4032 bytes: '_CODE' runs from 0x0200 to 0x48D9, past the 0x4000 boundary.
```

GB# reads the linker map itself and reports [GBS0310](../reference/diagnostics/banking.md#gbs0310-bank-0-overflowed) where the toolchain stays silent. The fix is always the same: move code or data out of the resident bank with `[Bank]`.

## Configuring the cartridge

Set `"mbc"`, `"romBanks"` and `"ramBanks"` in [gbsharp.json](../reference/gbsharp-json.md) to control the cartridge; left unset, a banked project gets MBC5 with battery-backed RAM, one 8 KB bank of save RAM behind that battery, and a ROM sized to fit. A budget on the bank count itself (for a smaller mapper, or a flash cart with a fixed size) is `[assembly: MaxROMBanks(n)]`, covered in [Memory and budgets](memory-and-budgets.md).

`Samples/Banking` is a working 64 KB cartridge using both `[Bank]` forms (explicit and automatic) plus banked assets, and the [Banking a big game](../tutorials/banking-a-big-game.md) tutorial walks through it.
