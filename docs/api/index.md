# Framework API reference

This is the API your game code compiles against: the `GBSharp.Framework` reference assembly.

Two things are worth knowing before you browse:

**The namespace is `GB`, not `GBSharp`.** Game code opens with `using GB;` (and usually `using static GB.Hardware;`), so the short name is what you type all day. Everything documented here lives under the [GB](GB.yml) namespace.

**Nothing in this assembly ever executes.** Every member is a declaration only. The GB# compiler maps each `[Native]` member to a C symbol in the GBDK backend during lowering. The method bodies you see in the assembly just throw. That means the API surface is exactly the set of operations the compiler knows how to translate to Game Boy hardware, no more.

## Where to start

- **[Hardware](GB.Hardware.yml)**: the static class most games import with `using static`; the frame loop, sprites, and joypad live here.
- **Display, Background, Window, Tiles, Text, Palettes, Metasprites, Audio**: one static class per hardware subsystem.
- **Banking, Budgets, Data**: ROM banking, resource budget attributes, and ROM data access.
- **FixedArray&lt;T&gt; / FixedList&lt;T&gt;**: the fixed-capacity collections that replace arrays and lists on a machine with no allocator.
- **Attributes**: `[Asset]`, `[Sprite]`, `[Font]`, `[Metasprite]`, `[Binary]`, `[Bank]`, `[Capacity]` and friends bind PNGs and binary files into ROM and shape where things live.

For task-oriented documentation, see the [guides](../guides/language-subset.md); this section is the per-member reference.
