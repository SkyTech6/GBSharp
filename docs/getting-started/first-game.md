# Your first game

This page goes from an empty directory to a ROM running in an emulator. It assumes the `gbsharp` tool and its toolchain from [installation](installation.md): `dotnet tool install --global gbsharp` then `gbsharp doctor --fix`. Working from a checkout instead, read every `gbsharp <command>` below as `dotnet run --project GBSharp.CLI -- <command>`.

## Create a project

```bash
gbsharp new MyGame --template sprite
```

`new` takes a template with `--template` (or `-t`), a machine with `--target` (`gb` for the original Game Boy, the default, or `gbc` for Game Boy Color), and a directory with `--out` (defaulting to one named after the project). It refuses a directory that already has files in it unless you pass `--force`: the one thing this command must never do is overwrite work.

There are three templates:

- **empty** (the default) is a `Main` that enables the display and runs the canonical GB# frame loop: `while (true)` with `Game.WaitVBlank()` at the bottom, and a comment marking where everything else goes.
- **sprite** is a sprite you move with the d-pad. Its tile data is written by hand as bytes in a `static readonly` array, which puts it in the cartridge rather than work RAM; the build report shows what it cost.
- **background** is a full-screen image loaded through the asset pipeline and scrolled. This is the only template that needs art, and rather than shipping a checked-in binary nobody can review, the CLI synthesises a placeholder PNG (`Assets/tiles.png`) when it writes the project.

The sprite template's `Program.cs`, in full:

```csharp
using GB;
using static GB.Hardware;

public static class Program
{
    // Two tiles of 2bpp data: 16 bytes each, in the cartridge because it
    // is 'static readonly'. The build report shows what it cost.
    private static readonly byte[] Shape =
    {
        0x3C, 0x3C, 0x42, 0x7E, 0x81, 0xFF, 0xA5, 0xFF,
        0x81, 0xFF, 0xBD, 0xFF, 0x42, 0x7E, 0x3C, 0x3C,
    };

    public static void Main()
    {
        Tiles.LoadSprite(0, 1, Shape);

        Display.Enable();
        Display.ShowSprites();

        byte x = 80;
        byte y = 72;

        Sprites[0].Tile = 0;

        while (true)
        {
            if (Input.Right) x++;
            if (Input.Left) x--;
            if (Input.Down) y++;
            if (Input.Up) y--;

            Sprites.Move(0, x, y);

            Game.WaitVBlank();
        }
    }
}
```

That is a complete game: it boots, draws a sprite, and responds to input. What the C# subset does and does not include is covered in [the language subset](../guides/language-subset.md).

## What new scaffolds

Every template writes the same frame around `Program.cs`:

- `gbsharp.json` is the project file, holding just the name and target to start with. See [project layout](project-layout.md) for what else can go in it.
- `MyGame.csproj` is for your **editor**, not the build. It references the GB# framework and analyzers through `GBSharp.Sdk`, so you get completion, navigation and GB# diagnostics as you type. Building it is an error by design; `gbsharp build` makes the ROM.
- `.gitignore` is one line, ignoring `build/`.
- `.vscode/tasks.json` has tasks for `gbsharp: build` (wired as the default build task, so Ctrl+Shift+B builds the ROM), `run`, `analyze` and `clean`. They shell out to the same `gbsharp` pipeline documented here, with the path to the CLI you ran `new` from baked in, so the editor and the terminal reach the exact same compiler.
- `.vscode/launch.json` makes F5 build and launch the emulator, with `Build only` and `Analyze (lint)` configurations beside it. This is not a real debug session: there is no GBZ80 debugger wired into VS Code, so the configurations run the CLI in a terminal. Source-level debugging happens in the emulator itself, from the `.sym` file written beside the ROM.

## Build it

```bash
gbsharp build MyGame
```

The build prints its stages (parsing, GB# validation, lowering, C generation, GBDK compilation, linking) and ends with the build report:

```
GB# Build Report
────────────────────────────────

Target                    Game Boy Color
ROM                       32.0 KB
WRAM used                 63 B / 4.0 KB
Static objects (declared) 38 B

ROM Banks
  Bank 0                  2.5 KB / 16.0 KB  ███░░░░░░░░░░░░░░░░░

Cycle estimates
  Frame budget            70,224 cycles @ 59.7 Hz
  Frame loop              130 cycles  ░░░░░░░░░░░░░░░░░░░░  0%

Call stack
  Deepest path            3 calls   Program.Main() -> Program.Setup() -> FixedList<Enemy>.Add
  Work RAM free           3.9 KB for stack and locals
```

Three things worth knowing on first read. **WRAM used** and **static objects declared** are different numbers on purpose: the first is what the linker actually placed, and the difference is the stack, shadow OAM and GBDK's own state. Reporting one number would be a useful-sounding lie. The **cycle estimates** are computed statically from the IR, so read them as ceilings for comparing changes, not as measurements; the frame budget is printed exactly because it is the only figure that is a fact. And the **call stack** depth is exact: GB# rejects delegates and has no function pointers, so the call graph is the complete account of what can reach what.

## Run it

```bash
gbsharp run MyGame
```

`run` builds and then launches the ROM in the bundled GB# Player, unless the project's `"emulator"` setting or `--emulator` says otherwise. `--emulator` takes `player` for the bundled Player, the id of a known debugging emulator, or a path to any executable. The emulators in the catalog load the `.sym` file GB# writes beside the ROM, so naming one is a first-class choice for source-level debugging rather than a workaround.

A missing emulator has never failed a build and still does not: the ROM is the deliverable and running it is a convenience, so `run` warns, tells you where the ROM is, and exits successfully.

## The loop

From here the loop is edit, then `gbsharp run` again. Two commands are worth knowing alongside it:

```bash
gbsharp analyze MyGame
```

checks the project without building a ROM (it needs no C toolchain at all, which is what makes it a fast CI lint job) and

```bash
gbsharp build MyGame --emit-c
```

keeps the generated C next to the ROM so you can see exactly what your C# became.

## Where everything lands

The ROM is written to `MyGame/build/MyGame.gb` (or `.gbc` for a Game Boy Color target), with the linker's map and symbol files beside it and, with `--emit-c`, the generated C under `build/c/`. The full inventory of the build directory is in [project layout](project-layout.md).

When the template stops being interesting, [move a sprite](../tutorials/move-a-sprite.md) builds a game up from the empty template, and [publishing](../guides/publishing.md) turns the result into something people without an emulator can run.
