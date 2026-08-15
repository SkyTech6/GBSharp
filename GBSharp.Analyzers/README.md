# GBSharp.Analyzers

Roslyn analyzers that report **GB#** diagnostics in the editor, before a build. The rules themselves live in `GBSharp.Rules` and are shared with the GB# compiler, so an id reported here means exactly what it means in a build.

This package exists so a scaffolded GB# project's design-time `.csproj` restores and diagnostics appear as you type. It is marked as a development dependency, so it never flows transitively to consumers.

You won't normally add this package by hand: `GBSharp.Sdk` references it for every GB# game project.

## Documentation

<https://skytech6.github.io/GBSharp/>; see the [diagnostics reference](https://skytech6.github.io/GBSharp/).

## Source

<https://github.com/SkyTech6/GBSharp>
