# gbsharp

The command-line driver for **GB#**, a statically compiled, hardware-aware C# development environment for the Game Boy and Game Boy Color. GB# compiles a constrained subset of C# to `.gb`/`.gbc` ROMs via GBDK-2020; there is no CLR, no JIT, and no garbage collector on the target.

## Install

```bash
dotnet tool install --global gbsharp
```

## Quick start

```bash
gbsharp doctor --fix
gbsharp new MyGame --template sprite
cd MyGame
gbsharp run
```

`doctor --fix` fetches the pinned GBDK-2020 and emulator runtime into a per-user cache, checksum-verified against the lock files that ship inside the tool.

## Documentation

<https://skytech6.github.io/GBSharp/>: getting started, tutorials, guides, and the [CLI reference](https://skytech6.github.io/GBSharp/reference/cli.html) for `build`, `run`, `analyze`, `profile`, and `publish`.

## Source

<https://github.com/SkyTech6/GBSharp>
