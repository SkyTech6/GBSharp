# GB# Thesis & Architecture

> **Reader's note.** This is the origin document: the proposal GB# was built
> from, kept for its rationale: the problem statement, the epistemics of cost
> estimation (§7.1), the anti-goals (§25), and the two-developer success
> criterion (§27). Phases 0–6 have since shipped, and some illustrative syntax
> here diverged on the way to real C# (`[RomBank]`/`[Banked]` became one
> `[Bank]`, `FixedList<Enemy, 8>` became `[Capacity(8)]`, `MyGame.gbproj`
> became `gbsharp.json`, and so on). §7.1 and §24's Phase 6 were updated in
> place; the rest keeps its original proposal voice. **Do not read API from
> this document**; the [README](README.md) shows the shipped surface, and
> [ROADMAP.md](ROADMAP.md) tracks what remains.

## Executive Summary

**GB#** is a statically compiled, hardware-aware C# development
environment for the Game Boy and Game Boy Color.

The core idea is deliberately *not* to run .NET, Mono, a garbage
collector, or a C# virtual machine on Game Boy hardware. Instead,
developers write games using a carefully constrained subset of C#. GB#
uses Roslyn to parse and analyze that source, lowers it into a small
platform-neutral intermediate representation, emits conservative C, and
delegates final compilation and linking to **GBDK-2020** and its
SDCC-based toolchain.

``` text
C# Source
    ↓
Roslyn
    ↓
GB# Validation + Semantic Analysis
    ↓
GB# Intermediate Representation
    ↓
GBDK C Backend
    ↓
Generated C
    ↓
GBDK-2020 / SDCC
    ↓
.gb / .gbc ROM
```

The thesis is that C#'s developer ergonomics can be separated from the
heavyweight runtime normally associated with C#. GB# retains the parts
that are useful at authoring time---strong typing, namespaces, methods,
structs, IDE completion, refactoring, analyzers, source navigation, and
compile-time tooling---while translating them into code appropriate for
an 8-bit SM83 target.

The result should feel modern to write while remaining explicit about
the realities of Game Boy hardware.

> **GB# is not ".NET for Game Boy." It is a statically compiled C#-like
> language and game framework for retro hardware, using Roslyn as the
> frontend and GBDK as the GB/GBC backend.**

------------------------------------------------------------------------

## 1. Product Thesis

Writing software for the Game Boy is constrained by the hardware rather
than by the expressiveness of modern programming languages.

C remains an effective language for the platform because it maps
predictably to the machine and is supported by mature toolchains such as
GBDK-2020. However, much of the friction in Game Boy development is
unrelated to the actual hardware constraints:

-   manual asset conversion
-   boilerplate around sprites and backgrounds
-   awkward ROM banking declarations
-   limited static analysis
-   weak resource visibility
-   build-tool configuration
-   primitive editor integration
-   repetitive low-level APIs
-   errors that are technically correct but poorly contextualized

GB# proposes that these problems can be addressed at compile time
without imposing a runtime abstraction cost.

The language therefore has two responsibilities:

1.  **Make Game Boy development substantially more pleasant.**
2.  **Make the cost of the developer's code more visible, not less
    visible.**

This distinction is fundamental. GB# should abstract boilerplate, not
hardware.

------------------------------------------------------------------------

## 2. Initial Platform Scope

### Version 1

GB# should initially target:

-   Nintendo Game Boy
-   Nintendo Game Boy Color
-   GBDK-2020
-   SDCC / GBDK's normal compilation and linking pipeline

The first implementation should not target Game Boy Advance.

GBDK-2020 is appropriate for GB/GBC but is not a GBA toolchain. GBA uses
an ARM CPU and would require a separate backend and toolchain such as
devkitARM/libgba or an equivalent ARM-based environment.

The compiler architecture should nevertheless avoid assuming that its
intermediate representation is inherently "GBDK C."

Long term:

``` text
                    ┌─ GBDK Backend ──────► GB / GBC
                    │
C# ─► Roslyn ─► IR ─┼─ GBA Backend ───────► GBA
                    │
                    └─ Future Backends ───► Other constrained platforms
```

This provides an expansion path without burdening the initial
implementation.

------------------------------------------------------------------------

## 3. Design Principles

### 3.1 C# Is the Authoring Language, Not the Runtime

There is no CLR on the target.

There is no JIT.

There is no garbage collector.

There is no requirement for the .NET Base Class Library on the target.

C# exists primarily as syntax, semantics, tooling, and a type system.

### 3.2 Hardware Constraints Are First-Class

GB# should understand concepts such as:

-   WRAM usage
-   HRAM usage
-   VRAM constraints
-   ROM banks
-   tile memory
-   sprite limits
-   integer width
-   expensive arithmetic
-   banked calls
-   asset size
-   stack usage
-   approximate cycle cost

These should appear in diagnostics and build reports.

### 3.3 Predictability Beats Cleverness

A developer should be able to understand what a piece of GB# code
becomes.

Where practical, GB# should expose generated C and generated assets.

The compiler should favor boring, predictable output over sophisticated
transformations that make performance difficult to reason about.

### 3.4 Compile-Time Abstraction Over Runtime Abstraction

Features that would normally require runtime machinery should preferably
be resolved during compilation.

Examples include:

-   asset references
-   fixed collections
-   generic specialization
-   ROM-bank placement
-   metadata
-   resource lookup
-   component registration

### 3.5 Escape Hatches Matter

Experienced retro developers will eventually need low-level access.

GB# should permit carefully designed interoperability with GBDK/C rather
than attempting to hide the underlying platform completely.

------------------------------------------------------------------------

## 4. Proposed Developer Experience

A minimal GB# game might look like:

``` csharp
using GB;

public static class Program
{
    public static void Main()
    {
        Display.Enable();

        byte x = 80;

        while (true)
        {
            if (Input.Right)
                x++;

            Sprites[0].X = x;

            Game.WaitVBlank();
        }
    }
}
```

The build process becomes:

``` text
gbsharp build

Parsing C#...
Validating GB#...
Lowering...
Processing assets...
Generating C...
Compiling with GBDK...
Linking ROM...

Output: bin/MyGame.gb
```

Development mode could provide:

``` text
gbsharp run
```

which builds the ROM and launches a configured emulator such as SameBoy.

Eventually, IDE integration could make **F5** perform the entire
pipeline.

------------------------------------------------------------------------

## 5. Compiler Architecture

### 5.1 Frontend: Roslyn

GB# should not implement a C# parser.

Microsoft Roslyn already provides:

-   syntax trees
-   semantic models
-   symbol resolution
-   overload resolution
-   type information
-   source locations
-   diagnostics infrastructure
-   analyzer infrastructure

The frontend pipeline should resemble:

``` text
.cs Files
   ↓
Roslyn Compilation
   ↓
Syntax Trees
   ↓
Semantic Models
   ↓
GB# Language Validation
   ↓
GB# Lowering
```

The semantic model is especially valuable because GB# needs to know
exactly what a source expression means rather than merely what its
syntax resembles.

For example:

``` csharp
player.Move(x);
```

can be resolved to a precise method symbol and parameter types before
GB# lowers it.

------------------------------------------------------------------------

## 6. The GB# Language Subset

GB# should be valid or near-valid C# source while deliberately
supporting only constructs that can be lowered predictably.

### Core Supported Types

Initial support should focus on:

``` text
byte
sbyte
ushort
short
bool
enums
structs
fixed-size arrays
```

`uint` and `int` may be supported but should produce performance
guidance where appropriate because 32-bit operations are substantially
more expensive on an 8-bit CPU.

### Core Language Features

Likely supported:

-   namespaces
-   static classes
-   structs
-   constrained classes
-   fields
-   methods
-   constructors that lower trivially
-   constants
-   enums
-   `if`
-   `switch`
-   `for`
-   `while`
-   `do`
-   `break`
-   `continue`
-   arithmetic and bitwise operators
-   simple properties
-   `ref` where lowering is deterministic
-   compile-time attributes

### Initially Unsupported or Restricted

Likely unsupported:

-   reflection
-   exceptions
-   `async` / `await`
-   arbitrary heap allocation
-   garbage collection
-   boxing
-   dynamic typing
-   LINQ
-   delegates
-   events
-   arbitrary interfaces
-   runtime generic dispatch
-   `System.String`
-   `List<T>`
-   `Dictionary<TKey,TValue>`
-   most of the .NET BCL

Unsupported features should fail with purpose-built GB# diagnostics
rather than leaking obscure backend compiler errors.

------------------------------------------------------------------------

## 7. Hardware-Aware Diagnostics

Diagnostics could become one of GB#'s strongest differentiators.

Instead of merely reporting language errors, the compiler can teach
developers about the target hardware.

Examples:

``` text
GBS0042: List<T> requires dynamic allocation.

Use FixedList<Enemy, 8> instead.
```

``` text
GBS0007: System.Int32 requires 32-bit arithmetic on SM83.

Consider ushort if values cannot exceed 65,535.
```

``` text
GBS0102: Multiplication of ushort values generates expensive code on SM83.
```

``` text
GBS0214: Enemy[32] reserves 256 bytes of WRAM.
```

``` text
GBS0301: LoadLevel() performs a banked call.
```

``` text
GBS0410: This loop runs up to 8 times at an estimated 3,500 cycles each, about 28,000 in total.
```

### 7.1 What a Cost Estimate May Claim

Cycle costs are estimated statically from the IR. That places a hard limit on
what they may say, and the limit is worth stating because the temptation is to
present the number as more than it is.

GB# emits C, and SDCC decides what the machine executes. The model therefore
cannot see register allocation, the peephole optimiser, constant folding after
GB# hands over, which way a branch goes, interrupt time, or the length of a
copy that is only known at runtime. Honest accuracy is roughly ±30–50% for
straight-line 8-bit work on locals, and a factor of two to five in either
direction once calls, struct copies or 16-bit arithmetic are involved.

Three rules follow, and they are what make the estimates worth shipping:

1.  **Comparative, not absolute.** A systematic error cancels out of a
    comparison. "Is this loop dearer than that one" and "did this change make
    things worse" survive the error bar; "this takes 2,140 cycles" does not.
2.  **Rounded, and always hedged.** Figures round to two significant figures and
    every message contains the word "estimated". Printing a precise number
    claims a precision the model does not have.
3.  **Refusal over invention.** Where a bound is not soundly knowable, GB#
    reports nothing rather than guessing. An unbounded loop is never given a
    total, and a cost that includes something unmeasurable says so.

The frame budget is the exception that anchors the rest: 70,224 cycles between
frames is a property of the hardware, not an estimate of SDCC's output, and it
is what gives every other figure a denominator worth quoting.

Diagnostics can have multiple severity levels:

-   error
-   warning
-   performance warning
-   resource warning
-   informational hint

Roslyn analyzers could surface many of these directly inside the editor
before the full ROM build occurs.

------------------------------------------------------------------------

## 8. Intermediate Representation

GB# should not translate Roslyn syntax directly into strings of C.

A small intermediate representation creates a clean boundary between
language semantics and platform code generation.

Possible primitives include:

``` text
IRModule
IRType
IRStruct
IRField
IRFunction
IRParameter
IRLocal
IRConstant
IRLoad
IRStore
IRCall
IRReturn
IRBranch
IRLoop
IRBinaryOperation
IRUnaryOperation
IRArray
IRAddress
```

The pipeline becomes:

``` text
C#
 ↓
Roslyn semantic representation
 ↓
GB# semantic validation
 ↓
GB# IR
 ↓
optimization / analysis
 ↓
target backend
```

This enables:

-   multiple target backends
-   centralized optimization
-   hardware cost analysis
-   deterministic C generation
-   compiler testing independent of GBDK
-   future direct code generation if ever desirable

------------------------------------------------------------------------

## 9. C and GBDK Backend

The first backend emits conservative C designed for GBDK.

For example:

``` csharp
public struct Player
{
    public byte X;
    public byte Y;

    public void Update()
    {
        if (Input.Left)
            X--;

        if (Input.Right)
            X++;

        Sprites.Move(0, X, Y);
    }
}
```

could lower approximately to:

``` c
typedef struct Player {
    uint8_t X;
    uint8_t Y;
} Player;

void Player_Update(Player* self)
{
    if (gb_input_left())
        self->X--;

    if (gb_input_right())
        self->X++;

    move_sprite(0, self->X, self->Y);
}
```

The generated C should be intentionally readable.

A command such as:

``` text
gbsharp build --emit-c
```

could retain the generated source for inspection.

This is useful for:

-   debugging
-   performance analysis
-   compiler development
-   experienced GBDK developers
-   understanding abstraction cost

------------------------------------------------------------------------

## 10. Framework API

GB# should provide a small standard framework designed specifically
around GB/GBC concepts.

Potential namespaces or systems include:

``` text
GB.Game
GB.Display
GB.Input
GB.Sprites
GB.Background
GB.Window
GB.Audio
GB.Tiles
GB.Palettes
GB.Memory
GB.Banking
GB.Assets
GB.Collections
```

The APIs should be thin compile-time or runtime wrappers around
efficient GBDK operations.

For example:

``` csharp
if (Input.Pressed(Button.A))
{
    Audio.Play(Sounds.Jump);
}
```

should not imply an elaborate input or audio object model.

------------------------------------------------------------------------

## 11. Fixed-Capacity Collections

Modern C# collection semantics generally assume dynamic memory.

GB# should instead provide explicitly bounded collections.

Example:

``` csharp
FixedList<Enemy, 8> enemies;
```

This could specialize at compile time into approximately:

``` c
typedef struct {
    Enemy items[8];
    uint8_t count;
} EnemyList8;
```

Other useful types might include:

``` text
FixedArray<T, N>
FixedList<T, N>
RingBuffer<T, N>
BitSet<N>
Pool<T, N>
```

Compile-time specialization makes generic syntax useful without
requiring runtime generic machinery.

Crucially, capacity remains visible in source code.

------------------------------------------------------------------------

## 12. Data-Oriented Game Architecture

GB# should avoid attempting to recreate Unity's `GameObject` /
`MonoBehaviour` model.

That abstraction is poorly suited to an 8-bit target and would encourage
allocation, indirection, and unpredictable execution.

A more appropriate style is data-oriented:

``` csharp
public struct Enemy
{
    public byte X;
    public byte Y;
    public byte Sprite;
    public EnemyType Type;
}

static FixedArray<Enemy, 16> enemies;
```

Systems then operate explicitly over that data:

``` csharp
public static void UpdateEnemies()
{
    for (byte i = 0; i < enemies.Length; i++)
    {
        ref Enemy enemy = ref enemies[i];
        EnemySystem.Update(ref enemy);
    }
}
```

The framework can remain approachable without disguising memory layout.

------------------------------------------------------------------------

## 13. Optional High-Level Game Lifecycle

A lightweight lifecycle abstraction could be offered for developers who
prefer an engine-like entry point:

``` csharp
public sealed class MyGame : Game
{
    public override void Start()
    {
        Background.Load(Level1);
        Player.Spawn(80, 72);
    }

    public override void Update()
    {
        Player.Update();
        Enemies.Update();
    }
}
```

This can lower to something as simple as:

``` c
void main(void)
{
    MyGame_Start();

    while (1)
    {
        MyGame_Update();
        vsync();
    }
}
```

The important requirement is that convenience APIs remain cheap and
understandable.

------------------------------------------------------------------------

## 14. Asset Pipeline

Asset processing is an opportunity for GB# to provide major
quality-of-life improvements.

A developer could write:

``` csharp
[Sprite("player.png")]
public static SpriteAsset Player;
```

or:

``` csharp
[Asset("forest.png")]
public static TileMap Forest;
```

At build time:

``` text
forest.png
    ↓
GB# Asset Pipeline
    ↓
validation
    ↓
palette conversion
    ↓
2bpp tile conversion
    ↓
tile deduplication
    ↓
map generation
    ↓
ROM placement
    ↓
generated C data
```

The game can then use:

``` csharp
Background.Load(Forest);
```

The compiler should report useful failures such as:

``` text
GBA100: player.png contains 6 colors.

The selected 2bpp conversion permits 4 colors per palette.
```

Asset processing can eventually include:

-   sprites
-   sprite sheets
-   tilesets
-   tilemaps
-   fonts
-   palettes
-   sound effects
-   music
-   binary data
-   level data

------------------------------------------------------------------------

## 15. ROM Banking

Banking is another area where compile-time tooling can substantially
improve the experience.

GB# could provide attributes such as:

``` csharp
[RomBank(3)]
public static class ForestLevel
{
}
```

or:

``` csharp
[Banked]
public static void LoadForest()
{
}
```

The compiler/backend can generate the necessary GBDK declarations and
conventions.

Eventually, automatic bank assignment could be possible:

``` csharp
[AutoBank]
public static class ForestAssets
{
}
```

The linker/build report could expose the result:

``` text
ROM BANK USAGE

Bank 0     13.2 KB / 16 KB
Bank 1     15.1 KB / 16 KB
Bank 2      8.7 KB / 16 KB
Bank 3     14.9 KB / 16 KB
```

Automatic placement should never make the resulting layout opaque.
Developers need to be able to inspect and override it.

------------------------------------------------------------------------

## 16. Resource Reporting

A successful build should communicate more than "compilation succeeded."

For example:

``` text
GB# Build Report
────────────────────────────────

Target                 Game Boy Color
ROM                    58.4 KB
WRAM                   2.1 KB
Static objects         1.4 KB
Stack reservation      512 B

Sprites                23
Background tiles       181
Window tiles           12

ROM Banks
  Bank 0               13.2 / 16 KB
  Bank 1               15.1 / 16 KB
  Bank 2                8.7 / 16 KB
  Bank 3               14.9 / 16 KB

Warnings               2
Performance warnings   1
```

The compiler should make constrained resources visible throughout
development.

------------------------------------------------------------------------

## 17. Project and Tooling Experience

A CLI could initially provide:

``` text
gbsharp new MyGame --target gb
gbsharp new MyGame --target gbc

gbsharp build
gbsharp run
gbsharp clean
```

Useful later commands might include:

``` text
gbsharp analyze
gbsharp assets
gbsharp banks
gbsharp size
gbsharp doctor
```

A project could resemble:

``` text
MyGame/
│
├─ MyGame.gbproj
│
├─ Program.cs
│
├─ Game/
│   ├─ Player.cs
│   ├─ Enemy.cs
│   └─ Levels.cs
│
├─ Assets/
│   ├─ player.png
│   ├─ forest.png
│   └─ jump.wav
│
└─ bin/
```

Longer term, GB# could integrate naturally with VS Code, Visual Studio,
and Rider.

------------------------------------------------------------------------

## 18. Proposed Repository Architecture

``` text
GBSharp/
│
├─ GBSharp.Compiler/
│   ├─ Frontend/
│   ├─ Validation/
│   ├─ Lowering/
│   ├─ IR/
│   ├─ Analysis/
│   └─ Diagnostics/
│
├─ GBSharp.Backend.GBDK/
│   ├─ CEmitter/
│   ├─ Banking/
│   ├─ Runtime/
│   └─ Toolchain/
│
├─ GBSharp.Framework/
│   ├─ Game.cs
│   ├─ Display.cs
│   ├─ Input.cs
│   ├─ Sprites.cs
│   ├─ Background.cs
│   ├─ Audio.cs
│   ├─ Banking.cs
│   └─ Collections/
│
├─ GBSharp.Assets/
│   ├─ Images/
│   ├─ Tiles/
│   ├─ Audio/
│   └─ Pipeline/
│
├─ GBSharp.Analyzers/
│
├─ GBSharp.CLI/
│
├─ GBSharp.Tests/
│   ├─ Compiler/
│   ├─ Backend/
│   ├─ Diagnostics/
│   └─ Integration/
│
└─ Samples/
```

A future GBA implementation becomes another backend rather than a
rewrite:

``` text
GBSharp.Backend.GBA/
```

------------------------------------------------------------------------

## 19. Interoperability

GB# should eventually permit direct access to GBDK where the framework
does not expose something.

Possible approaches include compile-time extern declarations:

``` csharp
[Native("move_sprite")]
public static extern void MoveSprite(byte id, byte x, byte y);
```

or a dedicated unsafe/native namespace.

The exact syntax can evolve, but the principle is important:

> GB# should provide a better default path without preventing developers
> from reaching the underlying platform.

This also makes incremental framework development possible. GB# does not
need wrappers for every GBDK API before it becomes useful.

------------------------------------------------------------------------

## 20. Debugging Strategy

Initial debugging can remain deliberately simple:

1.  generate readable C
2.  preserve source mapping information
3.  launch ROMs in established emulators
4.  provide compiler diagnostics tied to original C# locations

Later tooling could explore:

-   C# → generated C source maps
-   emulator integration
-   memory inspection
-   symbol maps
-   bank visualization
-   VRAM/tile inspection
-   performance instrumentation
-   frame-time/cycle budgets

GB# should leverage existing emulator capabilities rather than
attempting to build an emulator.

------------------------------------------------------------------------

## 21. Build Reproducibility

A GB# project should ideally pin or report:

-   GB# compiler version
-   GBDK version
-   target
-   asset compiler version
-   build configuration
-   backend configuration

This improves reproducibility and makes generated ROMs easier to
diagnose.

A future project file might resemble:

``` xml
<Project Sdk="GBSharp.Sdk">
  <PropertyGroup>
    <Target>gbc</Target>
    <GBDKVersion>...</GBDKVersion>
    <Optimize>true</Optimize>
  </PropertyGroup>
</Project>
```

The exact project format should not be finalized until the compiler
prototype establishes what metadata is actually required.

------------------------------------------------------------------------

## 22. Testing Strategy

The compiler should be tested at several layers.

### Semantic Tests

Input C# should either:

-   produce expected IR, or
-   produce the expected diagnostic

### Backend Snapshot Tests

Known IR should generate stable C output.

### Compilation Tests

Generated C should successfully compile with GBDK.

### ROM Integration Tests

Small sample ROMs should boot and perform known behavior.

Where automation permits, emulator-driven tests could eventually
validate memory or framebuffer state.

### Regression ROMs

Maintain tiny ROMs specifically testing:

-   arithmetic
-   loops
-   structs
-   arrays
-   calls
-   banked calls
-   sprites
-   input
-   tilemaps
-   asset loading

------------------------------------------------------------------------

## 23. MVP

The first milestone should prove the compiler architecture, not the
complete engine.

### MVP Goal

Compile the following concept into a ROM that boots in an emulator:

``` csharp
public static void Main()
{
    Display.Enable();

    byte x = 80;

    while (true)
    {
        if (Input.Right)
            x++;

        Sprites[0].X = x;

        Game.WaitVBlank();
    }
}
```

### Required MVP Features

-   load a C# project
-   parse with Roslyn
-   semantic validation
-   primitive `byte` and `bool`
-   local variables
-   static methods
-   method calls
-   `if`
-   `while`
-   simple arithmetic
-   basic framework intrinsics
-   basic IR
-   C emission
-   invoke GBDK
-   produce `.gb`
-   emulator launch command

### MVP Framework Surface

Only a handful of APIs are necessary:

``` text
Game
Display
Input
Sprites
```

No asset pipeline is required to prove the central hypothesis.

------------------------------------------------------------------------

## 24. Development Phases

### Phase 0 --- Spike

Prove:

``` text
C# → Roslyn → generated C → GBDK → bootable ROM
```

Hard-code framework mappings if necessary.

The objective is simply to prove the entire vertical pipeline.

### Phase 1 --- Language Core

Implement:

-   primitive types
-   structs
-   static classes
-   methods
-   control flow
-   arrays
-   enums
-   diagnostics
-   formal IR

### Phase 2 --- Core Framework

Implement:

-   display
-   input
-   sprites
-   backgrounds
-   tiles
-   palettes
-   basic audio
-   fixed collections

### Phase 3 --- Assets

Implement:

-   PNG conversion
-   sprite sheets
-   tilesets
-   tilemaps
-   palette validation
-   generated asset symbols

### Phase 4 --- Banking

Implement:

-   explicit banks
-   banked functions
-   asset banking
-   bank reports
-   optional automatic placement

### Phase 5 --- Developer Tooling

Implement:

-   Roslyn analyzers
-   IDE diagnostics
-   build reports
-   generated-C inspection
-   emulator launching
-   project templates

### Phase 6 --- Optimization and Profiling

This phase splits along a line the original "Explore" framing did not
anticipate. Some of it has exact answers and is ordinary implementation work;
the rest is a model whose output must be hedged. Treating the two the same
would either overclaim the estimates or underdeliver the facts.

Implement, because the answers are exact:

-   recursion detection --- a graph property, no accuracy claim to defend
-   worst-case call depth --- exact, because GB# has no delegates or function
    pointers, so the call graph is the whole account of what can reach what
-   memory analysis --- largely reporting polish, since phases 1 through 5
    already account for WRAM, ROM, VRAM and banks

Explore, and ship hedged:

-   cycle-cost estimation, subject to section 7.1
-   optimization and bank hints, limited to whole-program facts that a
    per-site diagnostic structurally cannot know

Deliberately not shipped, with reasons recorded in `ModuleAnalysis`: dead-code
hints (the lifecycle is entered without a visible call), per-expression costs
(expressions carry no source span), loop-invariant hoisting advice (needs
dataflow the structured IR does not support), and inlining advice (GB# does not
inline and SDCC's decision is invisible).

Resource visualization is satisfied by the build report's existing meter rather
than by a new surface. Section 16's illustrative `Sprites 23` row is not
produced: sprite ids are runtime values and there is no sprite budget symbol, so
there is no honest way to count them at compile time.

### Phase 7 --- Additional Backends

Only once GB/GBC is mature should the IR be used to explore:

``` text
GB# → GBA backend → ARM toolchain
```

------------------------------------------------------------------------

## 25. What GB# Should Not Become

Several tempting directions would undermine the project's strengths.

### Not a .NET Runtime Port

Trying to support normal .NET execution would introduce enormous runtime
complexity for very little benefit.

### Not Unity for Game Boy

The authoring experience can be modern without adopting Unity's runtime
architecture.

Avoid:

-   pervasive object hierarchies
-   reflection-driven components
-   dynamic allocation
-   invisible lifecycle machinery
-   runtime dependency injection

### Not a Hardware-Hiding Layer

If a developer allocates 25% of available WRAM, GB# should make that
obvious.

If a method causes a bank switch, GB# should make that discoverable.

If an operation is expensive on SM83, the tooling should say so.

### Not a Source of Absolute Cycle Counts

GB# estimates costs from its own IR. It does not run SDCC's register allocator,
it does not run the ROM, and it must not present a figure as though it had done
either.

This is the same principle as the three above, applied to the compiler's own
output rather than to the developer's. A confidently wrong number hides the
hardware more effectively than silence does, because it is quotable: it ends up
in a budget decision, and the developer has no way to tell it was a guess. The
tooling therefore rounds, hedges, and refuses to answer where it cannot answer
soundly. See section 7.1.

### Not a New Native Toolchain

GBDK already solves compilation, assembly, linking, libraries, and
platform integration.

GB# should initially stand on that ecosystem rather than replace it.

------------------------------------------------------------------------

## 26. Long-Term Opportunity

Once the basic compiler works, GB# can provide capabilities that are
difficult to achieve with a conventional C-only workflow.

Examples include:

-   compile-time asset references
-   automatic tile deduplication
-   typed level assets
-   static memory accounting
-   ROM-bank visualization
-   bank-aware call diagnostics
-   compile-time fixed collection specialization
-   cycle-cost estimates
-   automatic palette validation
-   IDE hardware warnings
-   source-level performance hints
-   C# source mapped to generated C
-   build-time resource budgets

A project could even declare explicit budgets:

``` csharp
[assembly: MaxWRAM(6144)]
[assembly: MaxROMBanks(8)]
[assembly: MaxSprites(32)]
```

and fail CI when they are exceeded.

This transforms GB# from a syntax convenience into a genuinely
hardware-aware development environment.

------------------------------------------------------------------------

## 27. Success Criteria

GB# succeeds if it can simultaneously satisfy two developers:

### The C# Developer

They should be able to start making a Game Boy game without first
becoming an expert in:

-   C build systems
-   GBDK macros
-   asset conversion tools
-   linker configuration
-   manual symbol generation

### The Retro Developer

They should still be able to answer:

-   Where is this data stored?
-   How many bytes does this consume?
-   Which ROM bank contains this?
-   Does this call switch banks?
-   What C was generated?
-   Why is this operation expensive?
-   Can I call the underlying GBDK API?

If GB# satisfies the first developer by frustrating the second, the
abstraction has gone too far.

------------------------------------------------------------------------

## 28. Core Architectural Thesis

The central bet behind GB# is simple:

**Modern language ergonomics do not require a modern runtime.**

Roslyn can provide the frontend.

GB# can provide restricted semantics, hardware awareness, compile-time
abstractions, diagnostics, asset processing, and a small intermediate
representation.

GBDK can provide the mature native backend for GB/GBC.

The Game Boy still receives small, predictable native code.

``` text
Modern Authoring Experience
           │
           ▼
          C#
           │
           ▼
     GB# Compiler
           │
     ┌─────┴─────┐
     │ Validation│
     │ Analysis  │
     │ Assets    │
     │ Banking   │
     └─────┬─────┘
           │
           ▼
         GB# IR
           │
           ▼
      GBDK Backend
           │
           ▼
     Conservative C
           │
           ▼
     GBDK-2020 / SDCC
           │
           ▼
       Game Boy ROM
```

GB# should therefore optimize for three things:

1.  **C# ergonomics at authoring time**
2.  **Game Boy constraints at compile time**
3.  **Predictable native code at runtime**

That combination is the project.
