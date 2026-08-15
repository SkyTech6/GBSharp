# GB#

A statically compiled, hardware-aware C# development environment for the Game Boy and Game Boy Color.

GB# is **not** ".NET for Game Boy". There is no CLR, no JIT, and no garbage collector on the target. You write a constrained subset of C#; GB# analyses it with Roslyn, lowers it to a small intermediate representation, emits conservative C, and hands that to **GBDK-2020** to produce a `.gb` or `.gbc` ROM.

```
C# → Roslyn → GB# validation → GB# IR → C → GBDK-2020 / SDCC → ROM
```

The bet is that modern language ergonomics do not require a modern runtime. See [GBSharp_Thesis_and_Architecture.md](GBSharp_Thesis_and_Architecture.md) for the full rationale.

**Documentation: <https://skytech6.github.io/GBSharp/>**: getting started, tutorials, guides, the CLI and diagnostics references, and the framework API. The site also publishes [llms.txt](https://skytech6.github.io/GBSharp/llms.txt) and [llms-full.txt](https://skytech6.github.io/GBSharp/llms-full.txt) for LLM-based tools.

## What it looks like

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

The whole `Sprites[0].X` chain erases to a single OAM store and the arithmetic stays 8-bit. GB# abstracts boilerplate, not hardware. Every build reports what the code costs (WRAM, ROM banks, estimated cycles against the 70,224-cycle frame budget), and `gbsharp profile` measures the real thing on the emulator GB# ships.

## Status

Phases 0 through 6 are implemented: the vertical pipeline, the language core, the framework, the asset pipeline, ROM banking, developer tooling, and the cost and stack analyses. Phase 7 (a GBA backend) is the only one untouched, and the IR is what would make it a backend rather than a rewrite. What remains open, and why, is tracked in [ROADMAP.md](ROADMAP.md).

## Getting started

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Making a game

GB# is a global `dotnet` tool named [`gbsharp`](https://www.nuget.org/packages/gbsharp) on nuget.org. From anywhere, with no checkout and no PowerShell:

```bash
dotnet tool install --global gbsharp
gbsharp doctor --fix
gbsharp new MyGame --template sprite
cd MyGame
gbsharp run
```

`doctor --fix` fetches the pinned GBDK-2020 and emulator runtime into a per-user cache, checksum-verified against the lock files that shipped inside the tool. Everything after that is the [documented `gbsharp` pipeline](https://skytech6.github.io/GBSharp/reference/cli.html): `build`, `run`, `analyze`, `profile`, `publish`.

Three packages ship alongside the tool and are never installed by hand: [GBSharp.Sdk](https://www.nuget.org/packages/GBSharp.Sdk), [GBSharp.Framework](https://www.nuget.org/packages/GBSharp.Framework) and [GBSharp.Analyzers](https://www.nuget.org/packages/GBSharp.Analyzers) are what the design-time `.csproj` written by `gbsharp new` restores, so an editor binds the code and reports GB# diagnostics as you type. A build uses none of them; the compiler carries its own framework copy inside the tool.

### Building GB# itself

For working on the compiler, framework, or tooling. GBDK-2020 and the emulator runtime are fetched into the checkout for you, checksum-verified against the same lock files:

```bash
pwsh tools/get-gbdk.ps1
pwsh tools/get-emulator.ps1
```

Build, test, and run a sample in the bundled GB# Player:

```bash
dotnet build GBSharp.slnx
dotnet test GBSharp.slnx
dotnet run --project GBSharp.CLI -- run Samples/Metasprite
```

Turn a sample into a game somebody else can run, with no emulator and no ROM file, or into a single `.html`:

```bash
dotnet run --project GBSharp.CLI -- publish win-x64 Samples/Metasprite
dotnet run --project GBSharp.CLI -- publish web --single-file Samples/Metasprite
```

From here, the documentation site takes over: [installation](https://skytech6.github.io/GBSharp/getting-started/installation.html), [your first game](https://skytech6.github.io/GBSharp/getting-started/first-game.html), [tutorials built on the samples](https://skytech6.github.io/GBSharp/tutorials/move-a-sprite.html), and [guides](https://skytech6.github.io/GBSharp/guides/language-subset.html) for the language subset, assets, banking, memory, diagnostics, profiling, and publishing.

## Repository

| Project | Role |
|---|---|
| `GBSharp.Rules` | The language-subset rules and every diagnostic, shared by compiler and analyzers. |
| `GBSharp.Analyzers` | Roslyn analyzers reporting GB# diagnostics in the editor. |
| `GBSharp.Sdk` | The project SDK a game's `.csproj` uses, so an editor understands the code. |
| `GBSharp.Compiler` | Roslyn frontend, validation, lowering, IR. Backend-agnostic. |
| `GBSharp.Assets` | PNG decoding, 2bpp conversion, deduplication, palettes. |
| `GBSharp.Backend.GBDK` | C emitter, runtime shim, GBDK toolchain driver, reporting. |
| `GBSharp.Framework` | Reference assembly game code compiles against. Declarations only. |
| `GBSharp.Emulator` | P/Invoke over the emulator runtime's C ABI and a thin managed wrapper. |
| `GBSharp.Player.Web` | The web player's HTML and JS, stamped into what `publish web` produces. |
| `GBSharp.CLI` | The `gbsharp` command. |
| `GBSharp.Tests` | Tests that run the ROMs they build, on an emulator GB# ships. |
| `Samples` | Nine working games, from `Hello` to a banked 64 KB cartridge. |
| `tools/GBSharp.DocsGen` | Generates the diagnostics reference and llms.txt for the docs site. |

The documentation site lives in [docs/](docs/); `pwsh docs/build-docs.ps1 -Serve` builds and previews it locally.

## License

[MIT](LICENSE). Games built with GB# are yours: nothing in the toolchain, the framework, or the runtime shim that gets compiled into your ROM imposes any obligation on what you ship.
