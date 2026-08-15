# GB#

A statically compiled, hardware-aware C# development environment for the Game Boy and Game Boy Color.

GB# is **not** ".NET for Game Boy". There is no CLR, no JIT, and no garbage collector on the target. You write a constrained subset of C#; GB# analyses it with Roslyn, lowers it to a small intermediate representation, emits conservative C, and hands that to **GBDK-2020** to produce a `.gb` or `.gbc` ROM.

```
C# → Roslyn → GB# validation → GB# IR → C → GBDK-2020 / SDCC → ROM
```

The bet is that modern language ergonomics do not require a modern runtime.

```csharp
using GB;
using static GB.Hardware;

public static class Program
{
    public static void Main()
    {
        Display.Enable();

        byte x = 80;

        while (true)
        {
            if (Input.Right)
                x++;

            Sprites[0].X = x;

            Game.WaitVBlank();
        }
    }
}
```

That compiles to C where the whole `Sprites[0].X` chain erases to a single OAM store and the arithmetic stays 8-bit. GB# abstracts boilerplate, not hardware.

## Start here

- **[Installation](getting-started/installation.md)**: the .NET SDK, `dotnet tool install --global gbsharp`, and `gbsharp doctor --fix` for everything else.
- **[Your first game](getting-started/first-game.md)**: `gbsharp new` to a running ROM in minutes.
- **[Tutorials](tutorials/move-a-sprite.md)**: worked walkthroughs of the sample games, from one moving sprite to a banked 64 KB cartridge.

## Find your way

- **[Guides](guides/language-subset.md)**: the C# subset, assets, banking, memory, diagnostics, profiling, publishing.
- **[CLI reference](reference/cli.md)**: every `gbsharp` command and option.
- **[gbsharp.json reference](reference/gbsharp-json.md)**: every project file key.
- **[Diagnostics reference](reference/diagnostics/index.md)**: all `GBSxxxx` ids, generated from the compiler's own definitions.
- **[Framework API](api/index.md)**: the `GB` namespace your game code compiles against, generated from the source XML docs.

## For language models

This site publishes an [llms.txt](https://skytech6.github.io/GBSharp/llms.txt) index and an [llms-full.txt](https://skytech6.github.io/GBSharp/llms-full.txt) containing the complete manual in one file, for use by LLM-based tools working with GB#.

## Design and internals

The user documentation lives here. The design rationale (why a compiled subset instead of a runtime, why the IR looks the way it does, and what GB# refuses to become) lives in [GBSharp_Thesis_and_Architecture.md](https://github.com/SkyTech6/GBSharp/blob/main/GBSharp_Thesis_and_Architecture.md) in the repository, alongside the [roadmap](https://github.com/SkyTech6/GBSharp/blob/main/ROADMAP.md).
