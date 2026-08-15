# The native escape hatch

Framework members are mapped to C symbols by attribute, and user code can use the same mechanism to reach anything GBDK exposes that the framework does not wrap:

```csharp
public static class Raw
{
    [Native("set_bkg_tile_xy")]
    public static void SetBackgroundTile(byte x, byte y, byte tile)
        => throw new System.NotSupportedException();
}
```

`Raw.SetBackgroundTile(3, 4, 7)` emits `set_bkg_tile_xy(3U, 4U, 7U);` and the C# declaration itself is never emitted. The body exists only to satisfy the C# compiler. There is no privileged path: the framework is written exactly this way. `GB.Background.Load` is a `[Native]` method that happens to ship in a box, so anything the framework can do, your code can do too.

A `[Native]` method has to be a shape the mapping can honour (static, with parameter and return types in the GB# subset), and one that is not is [GBS0053](../reference/diagnostics/language.md#gbs0053-invalid-native-declaration) rather than a C error later.

## Reaching GBDK

For a function GBDK's own headers declare, the declaration above is all there is. The generated C includes the GBDK headers, so SDCC already knows the prototype, and the call compiles like any other. Find the symbol name in GBDK-2020's documentation, write a `[Native]` method with matching parameters, done.

## Bringing your own C

The same attribute reaches functions of your own, in C files you supply. Two [gbsharp.json](../reference/gbsharp-json.md) keys carry them:

```json
{ "libraries": ["native/sram.c"], "includes": ["native/sram.h"] }
```

`"libraries"` names C source, object, or library files to hand to the linker: a prebuilt hUGEDriver, say, since GB# [owns no music engine](audio.md), or a `.c` file of your own. GB# has no opinion on what is in these files; it only links them, the way any C toolchain links a library the developer supplies.

`"includes"` is what makes the functions in them callable. The generated C only includes the GBDK and GB# runtime headers, and SDCC rejects a call to an undeclared function, so a `[Native]` symbol GBDK does not declare needs a prototype. Declare it in a header of your own and name that header under `"includes"`. The header is copied beside the generated C and included after the runtime shim, in every generated file, so every `[Native]` call site sees it.

```c
// native/sram.h
void sram_save(const uint8_t* data, uint8_t length);
```

```csharp
[Native("sram_save")]
public static void Save(ref byte data, byte length)
    => throw new System.NotSupportedException();
```

Both keys resolve relative to the project directory, the same as any other path in the file. A file that does not exist is an error at validation, before SDCC ever runs: [GBS0512](../reference/diagnostics/toolchain.md#gbs0512-include-not-found) for a missing header, [GBS0511](../reference/diagnostics/toolchain.md#gbs0511-library-not-found) for a missing library. That is because by the time a build reaches the linker, silently proceeding without a file you thought you linked is a worse failure than a clear upfront error.

## Where the line sits

The escape hatch is for reaching *functions*: GBDK's, or yours. It does not exempt the C# around the call from the [language subset](language-subset.md): the arguments still have to be types GB# can lower, and the costs still show up in the [build report](memory-and-budgets.md). That is the point of the design: the boundary is a symbol name, and everything on the C# side of it stays analysable.
