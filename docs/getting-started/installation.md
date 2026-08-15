# Installation

GB# has one requirement you install yourself: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everything else (the GBDK-2020 C toolchain and the emulator runtime) is fetched for you.

## Install the tool

GB# ships as a global `dotnet` tool on nuget.org:

```bash
dotnet tool install --global gbsharp
```

Then acquire the toolchain and check the install:

```bash
gbsharp doctor --fix
```

`doctor --fix` downloads the pinned GBDK-2020 and the emulator runtime into a per-user cache, checksum-verified against the lock files that ship inside the tool, and then reports what it found. That is the whole install: no checkout, no PowerShell, no submodule. Skip to [your first game](first-game.md).

Upgrading later is `dotnet tool update --global gbsharp`, and the full command set is in the [CLI reference](../reference/cli.md).

Three more packages go up with the tool, and you never install them by hand: `GBSharp.Sdk`, `GBSharp.Framework` and `GBSharp.Analyzers` are what the design-time `.csproj` that `gbsharp new` scaffolds restores, so your editor binds and analyses the code. `gbsharp build` uses none of them; the compiler carries its own copy of the framework inside the tool. See [GB# in the editor](../guides/ide-analyzers.md).

## Working from a checkout

The rest of this page is for building GB# itself, or for running the sample games the [tutorials](../tutorials/move-a-sprite.md) read. Inside a checkout the toolchain is fetched into the tree rather than the per-user cache, by two scripts in `tools/`, and the vendored copies win over the cache so the compiler you built is the one that runs.

If you have not installed the global tool, every command in these docs runs as `dotnet run --project GBSharp.CLI -- <command>` from the repository root instead; `gbsharp doctor` means `dotnet run --project GBSharp.CLI -- doctor`.

## Fetch the toolchain

GB# emits C and hands it to GBDK-2020 to produce the ROM. Fetch the pinned toolchain:

```bash
pwsh tools/get-gbdk.ps1
```

The script reads `tools/gbdk.lock.json`, downloads the GBDK-2020 4.5.0 archive for your OS and architecture, verifies its SHA256 against the lock file, and extracts it to `tools/gbdk`, which is gitignored. The pin is the point: every machine that runs the script gets the same bytes, and an archive that changes upstream (or gets tampered with in transit) fails the hash check loudly instead of quietly building against something different.

The script also checks that every tool GB# shells out to (`lcc`, `bankpack`, `romusage`) actually exists in the extracted archive. A tool missing from one platform's archive would otherwise be discovered as a link failure on that platform alone, which is the most expensive place to find it.

Re-running the script is a no-op when the pinned version is already installed and intact; pass `-Force` to re-download and re-extract. It runs under both Windows PowerShell 5.1 and PowerShell 7+ (`pwsh`) on Linux and macOS, so CI uses the same script on every platform, and so can you.

## Fetch the emulator runtime

The emulator runtime is what `gbsharp run` launches by default, what `gbsharp profile` measures with, and what the tests use to run the ROMs they build. Fetch it the same way:

```bash
pwsh tools/get-emulator.ps1
```

This script is deliberately the same shape as `get-gbdk.ps1`: same host detection, same SHA256 verification against a lock file (`tools/emulator.lock.json`), same version stamp, same install into a gitignored directory (`tools/emulator`). There is one acquisition story to learn rather than two. The archive ships two flavours of the runtime library (a regular one and an instrumented one that `gbsharp profile` needs) and the C header they both implement.

The runtime is built from [gbsharp-emulator](https://github.com/SkyTech6/gbsharp-emulator), a fork of [binjgb](https://github.com/binji/binjgb) that adds a stable C ABI and drops SDL from the core. That repository is a submodule here at `extern/gbsharp-emulator`, but **only for building the emulator itself**: the fetch script is how everyone else gets it, and you never need to clone the submodule.

## Build and test

Build everything:

```bash
dotnet build GBSharp.slnx
```

Run the tests:

```bash
dotnet test GBSharp.slnx
```

The test suite includes ROMs that are built and then run on the emulator. Tests that need the emulator runtime skip themselves when it is absent, so a bare checkout (no fetch scripts run at all) still runs green. The skips are telling you what was not exercised, not that something is broken.

## Troubleshooting: gbsharp doctor

If a build fails in a way that smells like a missing tool rather than a wrong program, ask the toolchain to describe itself:

```bash
gbsharp doctor
```

Doctor reports the GB# version, the .NET runtime, whether the framework assembly is where the compiler expects it, the GBDK root, version and compiler driver, and which emulator `gbsharp run` would launch. When everything is in place it ends with `Ready to build.` and exits zero.

When GBDK cannot be found, doctor lists the locations it searched and exits nonzero. Run `gbsharp doctor --fix` to fetch the pinned toolchain into the per-user cache (or `pwsh tools/get-gbdk.ps1` to vendor it into a checkout), or set `GBDK_HOME` to point at an existing GBDK-2020 installation. A `--gbdk-path` option on `doctor` (and on every command that builds) overrides `GBDK_HOME`, the vendored copy and the cache, for checking an install without committing to it.

With the toolchain in place, the next step is [your first game](first-game.md).
