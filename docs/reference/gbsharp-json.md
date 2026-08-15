# The project file (gbsharp.json)

A GB# project is described by a single `gbsharp.json` in the project root. The format is deliberately minimal, and deliberately not MSBuild: the project format is not settled until the compiler has shown what metadata it actually needs, and committing to an SDK now would fix the answer before the question is understood.

The file is optional. With none, everything is inferred from the directory: the project is named after the directory, targets the original Game Boy, compiles every `.cs` file under it, and looks for assets in `Assets/` and the project root. Property names are matched case-insensitively, and `//` comments are allowed.

Every relative path in the file (`assets`, `libraries`, `includes`) resolves against the project directory, so a project checked out anywhere still builds.

## name

**Type:** string. **Default:** the project directory's name.

The ROM name, used for the output file and the cartridge title. `gbsharp publish` also uses it as the published executable's file name and, when `player.title` is not set, the window title.

## target

**Type:** string, `"gb"` or `"gbc"`. **Default:** `"gb"`.

The machine to build for: `"gb"` for the original Game Boy, `"gbc"` for Game Boy Color. The `--target` command-line option overrides this, and the override goes through the same validation.

Parsing is deliberately strict: anything other than exactly `gb` or `gbc` is a validation error rather than a fallback to the default. Treating an unrecognised value as the default would turn a typo into a successful build of the wrong machine: a DMG ROM from a project that asked for colour, discovered only when the palettes are missing on hardware.

## emulator

**Type:** string. **Default:** unset.

What `gbsharp run` launches: a catalog id (`sameboy`, `bgb`, `emulicious`, `mgba`), `player` for the bundled GB# Player, or a path to an executable. Naming `player` explicitly lets a project want the bundled Player even on a machine with BGB installed.

`gbsharp run` resolves the emulator in order: the `--emulator` option, this setting, the `GBSHARP_EMULATOR` environment variable, a per-user `emulator.txt` in the GB# config directory, and then the bundled Player. This setting sits high in that order because the project file is the most specific statement, but a machine-specific absolute path belongs in the per-user file or the environment variable rather than here. A project that only runs on the machine that wrote it is not shareable, and the failure lands on whoever cloned it.

## exclude

**Type:** array of strings. **Default:** none.

Directories, relative to the project, to exclude from compilation. `bin`, `obj` and `build` are always excluded; this adds to that list. A build compiles every `.cs` file under the project directory that is not excluded (never what a `.csproj` says), so this is the only way to keep a source file out of the ROM.

## assets

**Type:** array of strings. **Default:** none.

Extra directories to search for `[Asset]` images. An asset path is looked for in the directory of the file that declared it, then `<project>/Assets`, then the project root, then these directories in order. The `Assets` folder is a convention rather than a requirement, and the project root is the fallback so a small game needs no folder at all.

## mbc

**Type:** string, one of `"none"`, `"mbc1"`, `"mbc5"`, `"mbc5+ram"`, `"mbc5+ram+battery"`. **Default:** see below.

The cartridge mapper. Only consulted when something is banked: left unset, a banked project gets MBC5 with battery-backed RAM (`mbc5+ram+battery`), which is what GBDK's own examples use and what most homebrew wants, and an unbanked project declares no mapper at all.

Parsing is strict for the same reason as `target`: a misspelled mapper that silently became `"none"` would produce a cartridge that cannot switch banks, and the symptom would be a game that runs until it loads a level. See [Banking](../guides/banking.md) for what banking is and when a project needs a mapper.

## romBanks

**Type:** integer, 2 to 512. **Default:** unset (the linker decides).

How many 16 KB ROM banks to reserve, counting bank 0. Unset lets the linker size the ROM to fit, which is the right answer until a project wants a fixed cartridge size.

## ramBanks

**Type:** integer, 0 to 16. **Default:** unset (the mapper decides).

How many 8 KB save RAM banks to reserve. Meaningful with a mapper that has RAM (`mbc5+ram`, `mbc5+ram+battery`); battery-backed RAM is what survives power-off, which is what a save file is.

Left unset, this follows the mapper: one bank for a mapper that names RAM, none for anything else. That is what keeps the two halves of the header telling the same story: the mapper is byte `0x147` and the RAM size is byte `0x149`, written by separate linker flags, and a cartridge that advertises a battery with nothing behind it is one an emulator offers to save and then has nowhere to put it.

Setting it explicitly to a value the mapper contradicts (`0` alongside `mbc5+ram+battery`, or a positive count alongside `mbc5`) is [GBS0513](diagnostics/toolchain.md#gbs0513-cartridge-ram-does-not-match-the-mapper), a warning. The ROM still builds; its header just describes a cartridge nobody made.

## libraries

**Type:** array of strings. **Default:** none.

External object or library files to link into the ROM, such as a prebuilt hUGEDriver, relative to the project directory. GB# does not own a music engine and has no opinion on what is in these files; it only links them, the way any C toolchain links a library the developer supplies.

A file named here that does not exist is a validation error before the build starts. By the time a build reaches the linker, silently proceeding without a library the developer thought they linked is a worse failure than a clear upfront error.

## includes

**Type:** array of strings. **Default:** none.

C header files to include in the generated C, relative to the project directory, so a `[Native]` method can call a function the framework does not wrap. The generated C only includes the GBDK and GB# runtime headers, and SDCC rejects a call to an undeclared function. A header named here is copied beside the generated sources and included after the runtime header, which is what lets a companion `.c` file under `libraries` expose functions to `[Native]` declarations.

Like `libraries`, a missing file is a clear upfront validation error rather than an implicit-declaration failure deep inside SDCC.

## player

**Type:** object. **Default:** unset.

How a published game presents itself: window title, size and the rest. These settings belong to the game rather than to the player: `gbsharp publish` writes them into the published executable, the Player reads them back at startup, and the Player has no UI that could disagree. See [Publishing a game](../guides/publishing.md).

| Key | Type | Default | What it does |
|---|---|---|---|
| `title` | string | the project name | The window title. A game that says nothing still gets a title, because the alternative is a window called "GB# Player", which tells the person playing it nothing about what they are playing. |
| `scale` | integer, 1 to 8 | Player's choice | Window size as a multiple of the Game Boy's 160x144 screen. |
| `fullscreen` | boolean | off | Open filling the screen. |
| `resizable` | boolean | on | Let the window be resized. |
| `integerScaling` | boolean | on | Scale by whole numbers only, so every pixel is the same size as every other pixel. |
| `volume` | integer, 0 to 100 | Player's choice | Output volume. |

Every setting is optional, and "not mentioned" stays different from "set to the default": only what the game actually said is written into the executable, which lets the Player's own defaults move without silently changing games that never expressed an opinion.

Out-of-range values (`scale`, `volume`) are caught when the project loads rather than by the Player, which reads them long after anyone could do anything about them: a window 40 screens wide is a typo in a project file, and the person who can fix it is the one running the command.

## diagnostics

**Type:** object mapping a diagnostic id or category to a severity. **Default:** none.

Diagnostic severity overrides, one id or a whole category at a time:

```json
{ "diagnostics": { "GBS0201": "none", "GBSharp.CycleCost": "none" } }
```

Accepted severities are `"none"`, `"error"`, `"warning"`, `"performance"`, `"resource"` and `"info"`. Two precedence rules, both for the same reason, the more specific statement wins:

- An id wins over a category that covers it.
- The project file wins over any `.editorconfig`.

Categories exist because bands arrive whole: a developer who does not want estimated cycle costs does not want any of them, and naming ids one at a time means editing the setting again every time GB# learns to report something new.

Diagnostics the compiler depends on stopping the build cannot be changed, and naming one by id is reported rather than ignored. A key that names no GB# diagnostic or category is a validation error: a setting for something that does not exist is almost always a typo, and silently ignoring it leaves the developer believing they configured something.

The ids and categories are catalogued under [Diagnostics](diagnostics/index.md); the interaction with `.editorconfig` is covered in [Configuring diagnostics](../guides/diagnostics-configuration.md).

## A complete example

```jsonc
{
  // Output file, cartridge title, and the published executable's name.
  "name": "MyGame",

  // Game Boy Color. Anything but "gb" or "gbc" is an error, not a fallback.
  "target": "gbc",

  // What `gbsharp run` launches. A catalog id, "player", or a path.
  "emulator": "bgb",

  // Compiled: every .cs under the project except bin, obj, build, and these.
  "exclude": ["Prototypes"],

  // Searched for [Asset] images after the declaring file's directory,
  // <project>/Assets, and the project root.
  "assets": ["Art/Exported"],

  // The cartridge. Only consulted because something is banked; unset, a
  // banked project gets mbc5+ram+battery and a ROM sized to fit.
  "mbc": "mbc5+ram+battery",
  "romBanks": 8,
  "ramBanks": 1,

  // Linked into the ROM, and the headers that declare what they expose.
  "libraries": ["native/hUGEDriver.lib"],
  "includes": ["native/hUGEDriver.h"],

  // Written into the published executable; the Player has no UI to disagree.
  "player": {
    "title": "My Game",
    "scale": 4,
    "fullscreen": false,
    "resizable": true,
    "integerScaling": true,
    "volume": 80
  },

  // One id turned off, one whole category. Id beats category; this file
  // beats .editorconfig.
  "diagnostics": {
    "GBS0201": "none",
    "GBSharp.CycleCost": "none"
  }
}
```
