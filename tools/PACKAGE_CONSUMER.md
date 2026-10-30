# Installed package acceptance

Run from the repository root with PowerShell 7 and .NET 10:

```powershell
./tools/get-gbdk.ps1
./tools/get-emulator.ps1
dotnet pack GBSharp.slnx -c Release -o artifacts/release-packages
./tools/test-package-consumer.ps1 -PackageDirectory artifacts/release-packages
```

Release CI passes its already packed directory and publishes those same files
after acceptance. Omitting `-PackageDirectory` builds and packs a fresh set first.
The harness rejects extra packages, duplicate IDs, and mismatched versions. It
records SHA256 hashes of the four accepted packages in its manifest and compares
the packages actually installed/restored against the staged bytes.

Each run creates an independent temporary consumer workspace outside the
checkout, with its own tool directory, NuGet package/cache directories,
`NuGet.Config`, and SDK resolver version pin. Source mapping allows GBSharp
packages only from the staged local feed; unrelated dependencies use nuget.org.
The generated projects must reference the staged version exactly. Pre-release
tool versions retain their suffix when scaffolding design-time references.

The harness restores and builds the design-time project for each template,
checks that the ordinary build emits `GBS0509`, injects an invalid `double` field
to prove the installed analyzer emits `GBS0002`, then builds all three ROMs via
the installed CLI. It copies `Samples/ComposedScrolling` and the browser SRAM
fixture into the consumer workspace and produces both web export formats there.
The tool's framework, lock files, and player assets must be present in the tool
installation. GBDK and the pinned emulator/WASM runtime remain external native
dependencies: `-GbdkPath` selects GBDK, while web publishing uses the runtime
cache and checksum-verified fetch path used by installed tools.

Default evidence is under `artifacts/package-consumer`: numbered subprocess
logs, published pages, `workspace.txt`, and `manifest.json`. The manifest has
absolute `sample.folder`, `sample.single`, `save.folder`, and `save.single` page
paths for the browser harness. Override the evidence directory with
`-ArtifactsDirectory`. Temporary workspaces are retained for failure inspection;
their exact path is recorded even when validation fails. Each subprocess has a
four-minute default timeout, kills its process tree on timeout, and saves output.
Use `-ProcessTimeoutSeconds` to adjust for slower hosts.
