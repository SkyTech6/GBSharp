# CLI commands

The `gbsharp` command line is how a GB# project is created, checked, built, run, measured and published.

It installs as a global `dotnet` tool, `dotnet tool install --global gbsharp`, which is how these pages assume you have it. Working from a checkout instead, every command runs as `dotnet run --project GBSharp.CLI -- <command>` from the repository root, and `gbsharp <command>` on these pages is shorthand for exactly that. See [installation](../getting-started/installation.md).

Most commands take a project directory as their first argument and default it to the current directory, so `gbsharp build` inside a project and `gbsharp build MyGame` from outside it do the same thing.

## gbsharp new

```
gbsharp new <name> [--template <template>] [--target <target>] [--out <dir>] [--force]
```

Creates a GB# project from a template, and one that builds: the scaffolded project compiles as written.

| Argument / option | Default | What it does |
|---|---|---|
| `<name>` | required | The project name. Also the ROM name and the cartridge title. |
| `--template`, `-t` | `empty` | `empty`, `sprite` or `background`. |
| `--target` | `gb` | The machine to build for: `gb` for the original Game Boy or `gbc` for Game Boy Color. |
| `--out`, `-o` | `<name>` | The directory to write into. |
| `--force` | off | Write into a directory that is not empty. |

Without `--force`, the command refuses a directory that already has files in it. The one thing `new` must never do is overwrite work.

The scaffolded project includes a `.csproj` so an editor can bind and analyse the code. `gbsharp build` never reads it (the compile set always comes from the directory and `gbsharp.json`), so a stale `.csproj` cannot produce a wrong ROM; a build reports the drift as a warning instead.

```
gbsharp new MyGame --template background
```

```
Created MyGame in G:\games\MyGame

  gbsharp build MyGame
```

## gbsharp build

```
gbsharp build [<path>] [--emit-c] [--emit-ir] [--annotate-source] [--out <dir>] [--target <target>] [--gbdk-path <dir>] [--report-json [<file>]]
```

Compiles a GB# project into a ROM: parses the C#, validates it against the GB# subset, lowers it to IR, generates C, and hands that to GBDK-2020. The ROM, the linker's map and the `.sym` symbol file land in the output directory.

| Argument / option | Default | What it does |
|---|---|---|
| `<path>` | `.` | The project directory. |
| `--emit-c` | off | Keep the generated C next to the ROM (in `build/c/`) so it can be inspected. |
| `--emit-ir` | off | Write the GB# intermediate representation alongside the ROM, as `<name>.gbir`. |
| `--annotate-source` | off | Comment every generated C statement with the C# line that produced it, and write `sourcemap.json` alongside the generated C. One code path produces both, so the comments and the JSON cannot disagree. |
| `--out`, `-o` | `<project>/build` | Output directory. |
| `--target` | project setting | Override the project target: `gb` or `gbc`. The override goes through the same validation as the project file's own setting. |
| `--gbdk-path` | see below | GBDK-2020 install root. Overrides `GBDK_HOME` and the vendored copy. |
| `--report-json [<file>]` | off | Write the build report as JSON. Given no value, it defaults to `<out>/report.json`. |

Every build ends with the build report: target, ROM size, WRAM used, per-bank usage, cycle estimates and the call stack analysis. The report and the JSON are built once and rendered twice: the terminal used to compute its own figures alongside the JSON, and the two had already drifted. `--report-json` carries the same numbers unrounded, plus the GB# and GBDK versions that produced them, which is what a CI script wants to check.

A declared resource budget (`[assembly: MaxWRAM(...)]` and friends) is checked against what the linker actually placed, and exceeding one fails the build, but the ROM is kept. That is deliberate: a budget can only be checked once the ROM exists, and the report is how a developer finds the bytes to remove.

```
gbsharp build Samples/Background --emit-c
```

```
Parsing C#...        1 file
Validating GB#...
Lowering...
Generating C...
Compiling with GBDK...
Linking ROM...

GB# Build Report
────────────────────────────────

Target                    Game Boy Color
ROM                       32.0 KB
WRAM used                 63 B / 4.0 KB
Static objects (declared) 38 B

ROM Banks
  Bank 0                  2.5 KB / 16.0 KB  ███░░░░░░░░░░░░░░░░░

Generated C: Samples/Background/build/c  (2 files)
```

The cycle-estimate and call-stack sections of the report are explained in [Profiling and cost](../guides/profiling-and-cost.md).

## gbsharp run

```
gbsharp run [<path>] [--emit-c] [--out <dir>] [--target <target>] [--gbdk-path <dir>] [--emulator <which>]
```

Builds a ROM and launches it. `<path>`, `--emit-c`, `--out`, `--target` and `--gbdk-path` mean what they mean for `build`.

`--emulator` names what to launch: `player` for the bundled GB# Player, `sameboy`, `bgb`, `emulicious`, `mgba`, or a path to an executable. The bundled Player runs a game; the catalog emulators debug one, and they load the `.sym` GB# writes, so naming one is a first-class choice rather than a workaround.

With no `--emulator`, the emulator is resolved in order: the project file's `"emulator"` setting, the `GBSHARP_EMULATOR` environment variable, a per-user `emulator.txt` in the GB# config directory, and then the bundled Player. The project file comes first because it is the most specific statement; the Player wins at the end on being reliably there, not on being better. Only if the Player has not been fetched are the catalog emulators searched for on `PATH` and in their usual install directories.

| Name | Emulator | Picks up the `.sym` itself |
|---|---|---|
| `player` | The bundled GB# Player | No, it is what a player runs, not a debugger |
| `sameboy` | SameBoy | Yes |
| `bgb` | BGB | Yes |
| `emulicious` | Emulicious | Yes |
| `mgba` | mGBA | No |

An emulator given as a path gets the plain treatment (the ROM path and nothing else), which is what every Game Boy emulator accepts. A machine-specific absolute path belongs in the per-user file or the environment variable rather than the project file: a project that only runs on the machine that wrote it is not shareable, and the failure lands on whoever cloned it.

A missing emulator has never failed a build and still does not. The ROM is the deliverable, and running it is a convenience:

```
gbsharp run Samples/Metasprite
```

```
Launched SameBoy
  Symbols alongside the ROM will be picked up for source-level debugging.
```

## gbsharp profile

```
gbsharp profile [<path>] [--out <dir>] [--target <target>] [--gbdk-path <dir>] [--frames <n>]
```

Builds a ROM, runs it headlessly on the instrumented flavour of the bundled emulator, and reports where the frame budget went, in your own methods. This is the measured counterpart of the cycle estimates the build report already prints: the estimate says what a method should cost from a walk over the IR, this says what it did cost, and the two disagreeing is information rather than a bug in either.

| Argument / option | Default | What it does |
|---|---|---|
| `<path>` | `.` | The project directory. |
| `--frames` | `600` | Frames to run. 60 is one second of emulated time. |
| `--out`, `-o`, `--target`, `--gbdk-path` | as for `build` | |

Cycles are attributed to C# methods through the same symbol chain every build writes: the `.sym` plus `<rom>.functions.json`. The same run also reports coverage: the profile says what the expensive code was, coverage says what code this run never reached and so proved nothing about.

Profiling needs the instrumented emulator runtime, fetched by `gbsharp doctor --fix` (or `tools/get-emulator.ps1` in a checkout). Nothing else in the CLI does, so this is the only command that says so when it is missing. See [Profiling and cost](../guides/profiling-and-cost.md) for reading the output.

## gbsharp publish

```
gbsharp publish [<rid>] [<path>] [--out <dir>] [--target <target>] [--gbdk-path <dir>] [--single-file]
```

Builds a ROM and wraps it in a standalone game that runs without an emulator. See [Publishing a game](../guides/publishing.md) for the full walkthrough.

| Argument / option | Default | What it does |
|---|---|---|
| `<rid>` | the host platform | The platform to publish for: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, or `web`. |
| `<path>` | `.` | The project directory. |
| `--out`, `-o` | `<project>/publish/<rid>` | Output directory. |
| `--single-file` | off | Web only: inline the runtime and the ROM into one `.html` that opens without a server. |
| `--target`, `--gbdk-path` | as for `build` | |

For the native platforms, the published executable is the prebuilt GB# Player for the target with the ROM and the window settings (from `gbsharp.json`'s `"player"` section) appended to it; the Player reads them out of its own file at startup. Nothing is relinked, which is why a game can be published for a platform this machine could not compile for, and why publishing takes about as long as copying a file. Signing, when it happens, has to happen after publishing: the appended bytes are part of what a signature covers. On Windows the game ships with `SDL2.dll` beside it.

For `web`, the output is a folder to upload to any static host. Browsers block module scripts and wasm fetches under `file://`, so that layout must be served over http, which is exactly the constraint `--single-file` exists to sidestep: one `.html` with the emulator and the ROM inlined, opening from a file manager with nothing else installed.

```
gbsharp publish win-x64 Samples/Metasprite
```

```
Published: Samples/Metasprite/publish/win-x64/Metasprite.exe
  124.1 KB, opens straight into the game
```

## gbsharp clean

```
gbsharp clean [<path>] [--out <dir>]
```

Deletes a project's build output: `<project>/build`, or the directory named by `--out`. A directory that does not exist is reported rather than treated as an error.

## gbsharp analyze

```
gbsharp analyze [<path>] [--target <target>]
```

Checks a project without building a ROM. This is `build` minus the backend: parsing, validation, lowering and asset conversion are all managed code, so it runs with no GBDK installed. That is the whole point: it makes a fast CI lint job possible, one that does not have to install a C toolchain to find out a project uses `List<T>`.

Diagnostics are reported exactly as a build would report them; a clean run prints how many files it found nothing wrong with. The diagnostics themselves are catalogued under [Diagnostics](diagnostics/index.md).

```
gbsharp analyze MyGame
```

```
No problems found in 3 files.
```

## gbsharp assets

```
gbsharp assets [<path>] [--target <target>]
```

Converts a project's assets and reports what they cost (tiles before and after deduplication, palettes, and bytes of ROM) without building a ROM. Like `analyze`, it needs no toolchain, which gives an artist working on a PNG a loop that does not involve a C compiler.

```
gbsharp assets MyGame
```

```
Assets
  Forest          forest.png      20x18   360 -> 9 tiles, 3 palettes          888 B
```

## gbsharp doctor

```
gbsharp doctor [--gbdk-path <dir>] [--fix]
```

Reports the state of the GB# toolchain: the GB# and .NET versions, the framework assembly, the GBDK root, version and compiler driver, and which emulator `gbsharp run` would launch. When GBDK cannot be found, it lists where it looked and exits non-zero, which makes it usable as a setup check in a script. See [Installation](../getting-started/installation.md) for fetching the pinned toolchain.

`--fix` fetches whatever is missing (GBDK-2020, the emulator runtime) into a per-user cache, checksum-verified against the pinned lock files. It is how an installed `gbsharp` tool acquires its toolchain on a machine with no GB# checkout; inside a checkout, the copies vendored under `tools/` keep winning.

```
gbsharp doctor
```

```
GB# doctor
────────────────────────────────

GB# version             1.0.0.0
.NET runtime            10.0.0
Framework assembly      .../GBSharp.CLI/bin/Debug/net10.0/GBSharp.Framework.dll
GBDK root               .../tools/gbdk
GBDK version            4.5.0
Compiler driver         .../tools/gbdk/bin/lcc
Emulator                GB# Player (.../tools/emulator/bin/gbsharp-player)

Ready to build.
```

## Locating GBDK

Every command that compiles resolves GBDK-2020 the same way: `--gbdk-path` if given, then the `GBDK_HOME` environment variable, then the vendored copy that `tools/get-gbdk.ps1` fetches, then the per-user cache that `gbsharp doctor --fix` fills. `doctor` reports which one won.
