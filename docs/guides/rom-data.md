# Data in ROM

A Game Boy has two places to keep data: the cartridge, which is large and read-only, and 8 KB of work RAM, which is neither. In GB# the keyword decides: `static readonly` means the cartridge; anything else means WRAM. The build report keeps the two apart, because they are different budgets with different failure modes.

```csharp
private static readonly byte[] TileData = { 0x00, 0x18, 0x24, 0x42, /* … */ };
private static byte frame;
```

The generated C annotates every static with where it went and what it cost:

```c
const uint8_t Program_TileData[16] = {
    0x00, 0x18, 0x24, 0x42, 0x42, 0x24, 0x18, 0x00, /* … */
};   /* 16 bytes, ROM */
uint8_t Program_frame;   /* 1 bytes, WRAM */
```

Those annotations are the same numbers the diagnostics report at build time: [GBS0203](../reference/diagnostics/memory.md#gbs0203-rom-allocation) for ROM data, [GBS0201](../reference/diagnostics/memory.md#gbs0201-static-allocation) for WRAM. So the cost of a declaration is visible in the editor, in the build output, and in the C, and they cannot disagree.

The rule of thumb falls out of the sizes: ROM is measured in banks of 16 KB and WRAM in single kilobytes shared with the stack, so data that never changes should say `readonly` and live in the cartridge. See [Memory and budgets](memory-and-budgets.md) for what the WRAM side costs.

## ROM data must be constant at build time

GB# writes static data into the ROM image while the project builds, so every element has to be known then. An initializer that can only be computed at runtime is refused ([GBS0057](../reference/diagnostics/language.md#gbs0057-initializer-is-not-constant)); assign the value in `Main` instead, which moves the array to WRAM where writes are possible.

## Writing to ROM is a compile error

A cartridge cannot be written to. SDCC would let the store through and the hardware would ignore it, or, on a real cartridge with a mapper, interpret it as a bank switch. GB# catches it in the frontend instead, where it can point at your C#:

```
Program.cs(9,9): error GBS0056: 'Program.TileData' is read-only data in ROM and cannot be assigned.
```

The fix depends on what you meant ([GBS0056](../reference/diagnostics/language.md#gbs0056-write-to-read-only-data)): copy the value into a mutable array or a local if it has to change while the game runs, or drop `readonly` to move the whole array into WRAM and pay for it there.

## `[Binary]` files

For data GB# has no opinion about (level layouts, a table another tool produced, anything already in the form your code wants), `[Binary]` copies a file into ROM unchanged:

```csharp
[Binary("level1.dat")]
private static BinaryAsset Level1;

byte first = Data.Read(Level1, 0);
```

Nothing is converted and nothing is validated beyond the file being there, which is exactly the service being offered. The alternative is a `static readonly byte[]` full of literals, which works and is unreadable past about twenty bytes.

`Data.Length(asset)` returns how many bytes the file held, and `Data.Read(asset, index)` reads one byte by index. `Read` is not bounds-checked, since the cartridge is not readable past its end, so the bounds are yours to keep, with `Length` as the ceiling. The bytes are reported like any other ROM cost ([GBS0622](../reference/diagnostics/assets.md#gbs0622-binary-asset-rom-cost)), and a file too large to place is an error ([GBS0615](../reference/diagnostics/assets.md#gbs0615-binary-asset-too-large)).

## ROM data and banking

Everything above describes data in bank 0, the 16 KB that is always mapped. `static readonly` data and `[Binary]` files can also be placed in switchable banks with `[Bank]`, at which point reading them directly becomes an error rather than a silently wrong load. [Banking](banking.md) covers the rules. Mutable statics cannot be banked at all ([GBS0306](../reference/diagnostics/banking.md#gbs0306-mutable-data-cannot-be-banked)): they live in WRAM, which is always mapped and is not banked on this hardware.
