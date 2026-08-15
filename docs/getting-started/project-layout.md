# Project layout

A GB# project is a directory of C# files with an optional `gbsharp.json` beside them. This page walks through what `gbsharp new` writes and what a build adds. A freshly scaffolded background-template project, after one build, looks like this:

```
MyGame/
  gbsharp.json          the project file
  MyGame.csproj         for the editor; the build never reads it
  Program.cs            the game
  Assets/
    tiles.png           art, found by [Asset] declarations
  .vscode/
    tasks.json          Ctrl+Shift+B -> gbsharp build
    launch.json         F5 -> gbsharp run
  .gitignore            ignores build/
  build/                everything a build produces
```

## gbsharp.json

The project file is deliberately minimal, and deliberately optional: with no `gbsharp.json` at all, everything is inferred from the directory: the ROM is named after the folder and the target is the original Game Boy. A new project starts with just:

```json
{
  "name": "MyGame",
  "target": "gb"
}
```

The keys, at a glance:

- `name` is the ROM name and the cartridge title. Defaults to the directory name.
- `target` is `"gb"` or `"gbc"`. Anything else is an error rather than a silent default, because a typo that quietly built for the wrong machine would only be discovered when the palettes are missing on hardware.
- `emulator` is a path to an emulator executable for `gbsharp run` to launch instead of the bundled Player.
- `exclude` lists directories, relative to the project, to leave out of compilation.
- `assets` lists extra directories to search for `[Asset]` images.
- `mbc`, `romBanks`, `ramBanks` describe the cartridge: which mapper, and how many ROM and save-RAM banks. Only consulted when something is banked; left unset, a banked project gets MBC5 with battery-backed RAM, a bank of save RAM to sit behind that battery, and a ROM sized to fit.
- `libraries`, `includes` are external object files to link into the ROM and C headers to include in the generated C, for reaching code the framework does not wrap.
- `player` describes how a published game presents itself: window title, scale, volume and the rest.
- `diagnostics` lists severity overrides, by id or by whole category.

Every relative path in the file resolves against the project directory, and every value with a fixed set of legal values is validated up front: a misspelled mapper or an out-of-range bank count is an error against `gbsharp.json` before anything compiles. The full key-by-key reference is at [gbsharp.json](../reference/gbsharp-json.md).

## Source files, and what exclude controls

A build compiles every `.cs` file under the project directory, recursively, in a deterministic order. There is no file list to maintain. Three directory names are always skipped: `bin`, `obj` and `build`, so the editor's output and GB#'s own output never get compiled back in. `"exclude"` adds your own names to that list; a path is skipped when any of its segments matches an entry, case-insensitively.

The `.csproj` exists so an editor can bind and analyse the code; `gbsharp build` never reads it and always enumerates sources itself. That is why the two can drift (MSBuild's default `**/*.cs` glob knows nothing about `"exclude"`), and why drift is reported as a warning rather than an error: a wrong `.csproj` compile set cannot produce a wrong ROM.

## Assets, and what assets controls

When a declaration says `[Asset("tiles.png")]`, the image is looked for first in the directory of the file that declared it, then in `Assets/`, then in the project root, then in each directory listed under `"assets"`. The `Assets` folder is a convention rather than a requirement, and the project root is the fallback so a small game needs no folder at all. `"assets"` is for art that lives somewhere else (a directory shared between projects, say) without copying it in. How the pipeline turns a PNG into tiles, maps and palettes is covered in [assets](../guides/assets.md).

## The build directory

Everything a build produces lands in `build/` (or wherever `--out` points), which is why the scaffolded `.gitignore` is one line. After a full-featured build it contains:

- `MyGame.gb` or `MyGame.gbc` is the ROM. Which extension you get follows the target.
- `MyGame.map`, `MyGame.sym`, `MyGame.noi` are the linker's map and symbol files, written beside the ROM on every build. The `.sym` is what debugging emulators pick up for source-level debugging, and together with `MyGame.functions.json`, it is the symbol chain `gbsharp profile` resolves measured cycles through.
- `c/` is the generated C, kept when you pass `--emit-c`. With `--annotate-source`, every generated statement carries a comment naming the C# line that produced it, and `c/sourcemap.json` holds the same mapping as data; one code path produces both, so they cannot disagree.
- `MyGame.gbir` is the GB# intermediate representation, written with `--emit-ir`.
- `report.json` is the build report as JSON, written with `--report-json`. It carries the same numbers the terminal report shows, unrounded, plus the GB# and GBDK versions that produced them, which is what a CI script should read.

`gbsharp clean` deletes the directory. Published games are separate output: they land under `publish/<rid>` and are covered in [publishing](../guides/publishing.md).
