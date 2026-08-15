# Running your game

```bash
gbsharp run MyGame
```

`gbsharp run` builds the ROM and launches it. With nothing configured, it launches the bundled **GB# Player**: the same player [`gbsharp publish`](publishing.md) wraps around a finished game, here running your latest build. It is what a player runs, not a debugger: it opens no settings screen, reads no symbol files, and shows the game exactly as a published copy would. It ships with the toolchain at a known path (fetched by `gbsharp doctor --fix`, or `tools/get-emulator.ps1` in a checkout), so it wins the default on being reliably there, not on being better.

When you want a debugger, name one.

## The named emulators

GB# knows how to find and launch the emulators a Game Boy developer is likely to have:

| Id | Emulator | Looked for as |
|---|---|---|
| `player` | The bundled GB# Player | Ships with the toolchain |
| `sameboy` | SameBoy | `sameboy`, `SameBoy`, `sameboy_sdl` |
| `bgb` | BGB | `bgb64`, `bgb` |
| `emulicious` | Emulicious | `Emulicious` |
| `mgba` | mGBA | `mgba`, `mgba-qt` |

The list is short on purpose: it exists so `gbsharp run` works without configuration, not to be exhaustive: any other emulator still works by giving its path. Executables are searched for on `PATH`.

The symbol column matters more than it looks. Every build leaves a `.sym` beside the ROM, and SameBoy, BGB and Emulicious pick it up on their own, so source-level debugging needs no setup at all: `gbsharp run` tells you when the emulator it launched will do this:

```text
Launched BGB
  Symbols alongside the ROM will be picked up for source-level debugging.
```

## Choosing one

For a single run, `--emulator` takes a catalog id or a path to any executable:

```bash
gbsharp run MyGame --emulator bgb
gbsharp run MyGame --emulator C:/tools/some-emulator.exe
```

An unrecognised executable is handed the ROM path and nothing else, which is what every Game Boy emulator accepts. `--emulator player` names the bundled Player explicitly, so a project can get it even on a machine with BGB installed.

For a per-project default, set the `"emulator"` key in [gbsharp.json](../reference/gbsharp-json.md), the same spellings, an id or a path:

```jsonc
{ "emulator": "sameboy" }
```

## Resolution order

GB# resolves the emulator from the most specific statement to the least:

1. `--emulator` on the command line
2. `"emulator"` in the project file
3. The `GBSHARP_EMULATOR` environment variable
4. A per-user setting: the path in `emulator.txt` under the `gbsharp` config directory (`%APPDATA%\gbsharp` on Windows, `$XDG_CONFIG_HOME/gbsharp` elsewhere)
5. The bundled GB# Player
6. The catalog emulators, searched for on `PATH`

A machine-specific absolute path belongs in the per-user file rather than the project: a project that only runs on the machine that wrote it is not shareable, and the failure lands on whoever cloned it.

If nothing at all can be launched, the build still succeeds: the ROM is the deliverable, and running it is a convenience. You get [GBS0505](../reference/diagnostics/toolchain.md#gbs0505-no-emulator-configured), a list of everywhere GB# looked, and the path to the ROM.
