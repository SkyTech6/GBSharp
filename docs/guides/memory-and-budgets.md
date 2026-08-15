# Memory and budgets

A Game Boy has 8 KB of work RAM, and everything mutable shares it: your static fields, the call stack, the shadow OAM the sprite system copies from, and GBDK's own state. There is no allocator rationing it and no protection between the parts: the stack starts at the top and grows down through the same bytes the statics grow up through. GB# cannot make the 8 KB bigger; what it can do is tell you the truth about it at every build, and fail the build when a number you declared is exceeded.

## Declared versus placed

Every static declaration has a knowable cost, and GB# reports it as it is written ([GBS0201](../reference/diagnostics/memory.md#gbs0201-static-allocation) for WRAM, [GBS0203](../reference/diagnostics/memory.md#gbs0203-rom-allocation) for ROM):

```
Program.cs(17,37): resource GBS0201: Program.enemies reserves 33 bytes of WRAM.
```

But the sum of your declarations is not what the machine uses. The real WRAM figure includes the stack, shadow OAM and GBDK's own state, none of which appear in your source, so the build report shows both numbers and never pretends one is the other:

```
WRAM used                 63 B / 4.0 KB
Static objects (declared) 38 B
```

Reporting only the declared figure would let a game creep past its real footprint and still look fine; reporting only the placed figure would leave you unable to see which declaration is responsible. The difference between them is itself information: it is the overhead everything else contributes. The same two-number honesty applies to ROM banks, where the gap is the code the linker put there. See [Banking](banking.md).

The report also bounds the stack the only way that is exact on this toolchain: in calls, not bytes. GB# rejects delegates and has no function pointers, so the call graph is complete and the depth is a fact; a byte figure would require modelling SDCC's frame layout and would be wrong in the optimistic direction, which is the one that lets a ROM ship and then corrupt memory.

```
Call stack
  Deepest path            3 calls   Program.Main() -> Program.Setup() -> FixedList<Enemy>.Add
  Work RAM free           3.9 KB for stack and locals
```

## Budgets that fail the build

A budget nobody enforces is a comment. GB#'s budgets are assembly attributes, and exceeding one fails the build:

```csharp
[assembly: MaxWRAM(6144)]
[assembly: MaxROMBanks(8)]
```

```
Budgets
  WRAM                    18 B / 8 B  EXCEEDED

error GBS0210: This game uses 18 bytes of work RAM; the declared budget is 8.
```

Three are available:

- **`[assembly: MaxWRAM(bytes)]`**: the most work RAM the game may use ([GBS0210](../reference/diagnostics/memory.md#gbs0210-wram-budget-exceeded)).
- **`[assembly: MaxROM(bytes)]`**: the largest ROM image the game may produce ([GBS0211](../reference/diagnostics/memory.md#gbs0211-rom-budget-exceeded)).
- **`[assembly: MaxROMBanks(banks)]`**: the most 16 KB banks the cartridge may declare, counting bank 0 ([GBS0212](../reference/diagnostics/memory.md#gbs0212-rom-bank-budget-exceeded)). Useful where cartridge size is a cost rather than a limit: a smaller mapper, or a flash cart with a fixed budget.

Budgets are checked against what the linker placed, not what the code declared. This is the load-bearing detail: a declared-bytes check would exclude the stack, shadow OAM and GBDK's own state, so a game could creep past its budget and still pass. Checking the linker map means the budget holds against the number the hardware will actually see, which is the whole value of declaring one: it holds while nobody is looking.

When a budget fires, the fix is either honest revision (raise the number, because it was optimistic) or an actual saving: move data into ROM by making it `static readonly` ([Data in ROM](rom-data.md)), shrink a `[Capacity]`, or pack banks more tightly.

## Budgets in CI

`--report-json` writes the same numbers the report prints, unrounded, plus the exact call depth, with the GB# and GBDK versions that produced them, for a CI script to check:

```bash
gbsharp build --report-json
```

The budget attributes already fail the build on their own, so the simplest CI enforcement is to declare them in the source and let `gbsharp build` exit nonzero. The JSON is for the checks a build error cannot express: trend lines, a call-depth ceiling, comparing two branches. Its sections are absent rather than zeroed when there is nothing to say, and additions are nullable, so a script written against schema version 1 reads what it always did. See the [CLI reference](../reference/cli.md) for the schema.

## VRAM has a budget too

Video memory is not WRAM, but it is just as fixed: background and window share one 256-tile region, and sprites have their own. GB# sums every asset's unique tiles because it can see them all, and reports the total against the region ([GBS0204](../reference/diagnostics/memory.md#gbs0204-vram-tile-budget)). A total above the region is fine when screens replace each other at runtime: GB# cannot see load order, so it reports the sum rather than failing the build. A *single* asset larger than the region is an error ([GBS0205](../reference/diagnostics/memory.md#gbs0205-vram-tile-budget-exceeded)), because no load order can make it work.
