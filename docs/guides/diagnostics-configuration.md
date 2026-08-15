# Configuring diagnostics

GB# reports what your code costs at every build: WRAM, ROM, estimated cycles, bank switches. A diagnostic nobody can silence is a diagnostic that eventually gets ignored wholesale: [GBS0201](../reference/diagnostics/memory.md#gbs0201-static-allocation) fires on every static field a program declares, and a developer who has accepted their WRAM budget needs a way to stop hearing about it without also stopping hearing about the next thing.

So anything that only describes a cost can be turned down, in `.editorconfig` or in the project file, one id or a whole category at a time.

## Severities

A diagnostic is reported at one of five severities, and configuration can move it to any of them, or to `none` to silence it entirely:

| Severity | Meaning |
|---|---|
| `error` | Compilation cannot continue. |
| `warning` | The code is suspect. |
| `performance` | The code is correct but costs more than it looks like it does. |
| `resource` | The code consumes a constrained resource: WRAM, VRAM, ROM, sprites. |
| `info` | Informational only. |

`performance` and `resource` are distinct from `warning` because they say something about the hardware rather than about the code's correctness. The [diagnostics reference](../reference/diagnostics/index.md) lists every id, its default severity, and whether it can be configured.

## In the project file

The `"diagnostics"` key in [gbsharp.json](../reference/gbsharp-json.md) maps an id or a category to a severity:

```jsonc
{ "diagnostics": { "GBS0201": "none", "GBSharp.CycleCost": "none" } }
```

A category is there because bands arrive whole: a developer who does not want estimated cycle costs does not want any of them, and naming them one at a time means editing the setting again every time GB# learns to report something new. The categories match the id bands:

| Category | Ids | Covers |
|---|---|---|
| `GBSharp.Language` | GBS0001–0099 | Constructs outside the GB# language subset |
| `GBSharp.Performance` | GBS0100–0199 | Operations that are expensive on SM83 |
| `GBSharp.Memory` | GBS0200–0299 | WRAM, VRAM and ROM consumption |
| `GBSharp.Banking` | GBS0300–0399 | ROM banking |
| `GBSharp.CycleCost` | GBS0400–0499 | Estimated cycle costs |
| `GBSharp.Toolchain` | GBS0500–0599 | The toolchain and the build itself |
| `GBSharp.Assets` | GBS0600–0699 | The asset pipeline |

The `GBSharp.` prefix is optional in the project file (`"CycleCost"` means the same thing) because in a file that is entirely GB# settings, the prefix only says what the file already knows.

A setting that names something that does not exist is almost always a typo, so it is rejected as an invalid project file rather than silently ignored: silently ignoring it would leave you believing you configured something.

## In .editorconfig

The same settings in the standard Roslyn spellings, which also configure the [editor analyzers](ide-analyzers.md):

```ini
[*.cs]
dotnet_diagnostic.GBS0201.severity = none
dotnet_analyzer_diagnostic.category-GBSharp.CycleCost.severity = none
```

GB# reads `.editorconfig` with Roslyn's own parser, so nesting, globs and section precedence behave exactly as they do for `CS` ids, and every `.editorconfig` from the project directory up to the filesystem root is considered, nearest winning. The per-id form takes Roslyn's severity vocabulary (`none`, `silent`, `error`, `warning`, `suggestion`); the category form accepts GB#'s full scale as well, including `performance` and `resource`.

## Precedence

An id wins over a category, the same way the project file wins over an `.editorconfig`: the more specific statement. In full, from most to least specific:

1. An id in `gbsharp.json`
2. A category in `gbsharp.json`
3. An id in `.editorconfig`
4. A category in `.editorconfig`
5. The diagnostic's declared default

So `{ "GBSharp.CycleCost": "none", "GBS0401": "performance" }` silences the whole cycle-cost band except the frame-loop figure, which is usually the one worth keeping.

## What cannot be suppressed

Anything the compiler depends on stopping the build cannot be configured. Downgrading [GBS0042](../reference/diagnostics/language.md#gbs0042-dynamic-collection) would not make `List<T>` work; it would produce C that compiles and does the wrong thing. Asking is answered rather than ignored:

```text
warning GBS0508: GBS0043 cannot be suppressed or downgraded, and the setting for it was ignored.
```

That is [GBS0508](../reference/diagnostics/toolchain.md#gbs0508-diagnostic-cannot-be-suppressed), and it only answers a setting that names a non-suppressible diagnostic *by id*. Muting a whole category is never refused: a blanket statement about a band is not a claim about any particular member of it, and refusing it would mean a developer muting a category could be scolded for a descriptor they have never heard of.

Which diagnostics are configurable is marked per id in the [diagnostics reference](../reference/diagnostics/index.md): as a rule, costs and resource notes can be configured freely, and errors cannot.
