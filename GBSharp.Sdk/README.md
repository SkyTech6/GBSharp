# GBSharp.Sdk

The MSBuild project SDK for **GB#** games. Gives a game project the framework reference, the analyzers, and an editor that understands the code, in three lines of `.csproj`:

```xml
<Project Sdk="GBSharp.Sdk/1.0.0">
</Project>
```

Carries no GB# build configuration; that stays in `gbsharp.json`, read by the `gbsharp` CLI tool.

`gbsharp new` scaffolds a project against this SDK for you, pinned to the version of the `gbsharp` tool that scaffolded it, so you won't normally reference it by hand.

## Documentation

<https://skytech6.github.io/GBSharp/>

## Source

<https://github.com/SkyTech6/GBSharp>
