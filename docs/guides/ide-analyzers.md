# GB# in the editor

GB# diagnostics live in the editor, before any build. Type `List<byte>` and the squiggle appears as you type it, with the same id, the same message and the same suggested alternative a build would print: `List<T>`, `string`, delegates, interfaces, LINQ, and what a static field costs are all reported live.

Two pieces make that work: an MSBuild SDK that lets an editor understand a game project, and a set of Roslyn analyzers that share their rules with the compiler.

## The project SDK

A game project scaffolded by `gbsharp new` gets a `.csproj` that uses `GBSharp.Sdk`. The project exists so an IDE can bind and analyse the code: it is not how a ROM is produced; `gbsharp build` is. The SDK sets up what an editor needs and nothing else:

- **`netstandard2.0`**, because game code is never executed by .NET. It only has to bind, and the lowest common target keeps the reference set small enough that nothing in the BCL looks available when it is not.
- **`build/` excluded from compilation**, because build output is not source, and MSBuild's defaults do not know that.
- **`gbsharp.json` passed to the analyzers** as an `AdditionalFile`, which is the cached, deterministic path and the only one available to code that must not touch disk. That is how your [diagnostic configuration](diagnostics-configuration.md) reaches the editor.
- **`CS0649` silenced**, because an `[Asset]` field is written by the build, never in source.

The SDK deliberately carries no GB# configuration. The target, name, emulator, banks and diagnostics all live in [gbsharp.json](../reference/gbsharp-json.md), which stays the single source of truth; duplicating any of it in MSBuild properties would create two places to disagree.

## Why `dotnet build` is blocked

Running `dotnet build` on a game project fails, by design:

```text
error GBS0509: This project exists so an editor can analyse your code. Build the ROM
with 'gbsharp build' instead. (Set GBSharpAllowManagedBuild=true to override.)
```

The guard makes "design-time only" a fact rather than a comment. Design-time builds (what IntelliSense and live analysis run) are exempt, so the editor keeps working completely; what the guard stops is `dotnet build` quietly producing a netstandard assembly that is not a ROM and cannot become one. GBS0509 is raised by MSBuild rather than by the compiler, which is why it does not appear in the [diagnostics reference](../reference/diagnostics/index.md): it can only ever fire from a build GB# never runs.

For the same reason, `gbsharp build` never reads the `.csproj`: it enumerates the project's source files itself. If the two views of "the files in this project" drift apart, [GBS0507](../reference/diagnostics/toolchain.md#gbs0507-project-file-drift) says so, a warning rather than an error, because a wrong `.csproj` cannot produce a wrong ROM.

## Shared rules, guaranteed parity

The analyzers and the compiler share their rules through `GBSharp.Rules`, a project that targets netstandard2.0 and touches no files, so both read one definition. An id means the same thing in both places: the [GBS0042](../reference/diagnostics/language.md#gbs0042-dynamic-collection) in your editor is the GBS0042 a build reports, down to the message.

The guarantee runs one direction on purpose: a test asserts the analyzer's ids are a **subset** of what a build reports, never a superset. The editor may miss things only whole-program analysis can see (banking conflicts, cycle totals, the linker's placements) but it will never invent a diagnostic the build would not confirm. An editor that cried wolf would teach you to ignore it.

## `gbsharp analyze`: the CI lint

The same checks run from the command line, without building a ROM:

```bash
gbsharp analyze MyGame
```

Everything it needs (parsing, validation, lowering, asset conversion) is in managed code, so it runs with no GBDK installed. That is the whole point: a CI lint job should not have to install a C toolchain to find out a project uses `List<T>`, and an artist working on a PNG gets a loop that does not involve a C compiler. It exits non-zero on any error, which is all a CI step needs.

See the [language subset guide](language-subset.md) for what the diagnostics enforce, and the [CLI reference](../reference/cli.md) for the command's options.
