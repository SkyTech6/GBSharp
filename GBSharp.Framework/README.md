# GBSharp.Framework

The reference assembly that **GB#** game code compiles against. Members here are declarations only: their bodies never execute and are never lowered. The GB# compiler maps each `[Native]` member to a C symbol in the target backend.

This package exists so a scaffolded project's design-time `.csproj` restores and the editor gets IntelliSense against the framework API. `gbsharp build` never uses it: the compiler carries its own copy of this assembly inside the tool.

You won't normally add this package by hand: `GBSharp.Sdk` references it for every GB# game project.

## Documentation

<https://skytech6.github.io/GBSharp/>; see the [framework API reference](https://skytech6.github.io/GBSharp/).

## Source

<https://github.com/SkyTech6/GBSharp>
