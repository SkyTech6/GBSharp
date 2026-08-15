# The language subset

GB# compiles a constrained subset of C#. The constraint is not a limitation waiting to be lifted: it is the design. Every construct in the subset lowers predictably to C an 8-bit CPU can run, with no runtime underneath it: no CLR, no JIT, no garbage collector. Anything that would need one is refused at compile time, in GB#'s own words, with an alternative.

This page is the map of that boundary: what is inside, what is outside and why, and the two places where GB#'s syntax deviates from the thesis that motivated it.

## What is supported

**Types.** `byte`, `sbyte`, `ushort`, `short`, `bool`, enums, structs, and fixed-size arrays. These are the types the SM83 can work with honestly: 8-bit values are native, 16-bit values cost more and the build says so, and anything wider is refused ([GBS0002](../reference/diagnostics/language.md#gbs0002-unsupported-type)). `int` arithmetic that survives into the output is reported as a performance cost ([GBS0007](../reference/diagnostics/language.md#gbs0007-32-bit-arithmetic)): consider `ushort` if values cannot exceed 65,535, or `byte` if they cannot exceed 255.

**Control flow.** `if`, `while`, `do`, `for`, `switch`, and the full operator set. `while (true)` is the canonical GB# game loop.

**Structure.** `ref` parameters, static classes, struct methods, properties, and constructors. A struct is a layout and its methods are functions that take a pointer to one: see [Structs](structs.md) for what that model buys and what it asks of you.

**Data.** `static readonly` arrays are placed in ROM rather than work RAM: see [Data in ROM](rom-data.md). Array lengths must be compile-time constants ([GBS0052](../reference/diagnostics/language.md#gbs0052-array-size-must-be-constant)), because GB# reserves the storage at compile time.

`Samples/Enemies` exercises the whole core in one small program that covers structs, enums, fixed collections, arrays, `ref` parameters, `for`, `switch` and 8-bit arithmetic:

```csharp
public static class EnemySystem
{
    public static void Update(ref Enemy enemy, byte frame)
    {
        switch (enemy.Kind)
        {
            case EnemyKind.Walker:
                enemy.X++;
                break;

            case EnemyKind.Flyer:
                enemy.X++;
                // A shift, not a divide: the cost should be obvious from the source.
                enemy.Y = (byte)(64 + ((frame >> 3) & 7));
                break;

            case EnemyKind.Turret:
                break;
        }

        if (enemy.X > 160)
        {
            enemy.X = 0;
        }
    }
}
```

## What is refused, and why

Everything on this list needs machinery the Game Boy does not have: a heap, an allocator, an object header, a dispatch table, a scheduler, unwinding. GB# refuses each one by name, pointing at your C#, with the alternative in the message:

```
Program.cs(12,29): error GBS0042: List<byte> requires dynamic allocation.
        public static List<byte> Items = new List<byte>();
                      ^^^^^^^^^^
    Use FixedList<T, N> or FixedArray<T, N> instead. Capacity stays visible in the
    source, and the storage is reserved at compile time.
```

| Refused | Id | Why |
|---|---|---|
| `List<T>` and other dynamic collections | [GBS0042](../reference/diagnostics/language.md#gbs0042-dynamic-collection) | Requires dynamic allocation. Use `FixedList<T>` or `FixedArray<T>`, below. |
| `System.String` | [GBS0043](../reference/diagnostics/language.md#gbs0043-systemstring-is-unavailable) | Requires heap allocation. Use a fixed byte array, and the tile-based text APIs to draw it: see [Drawing text](text.md). |
| Exceptions | [GBS0044](../reference/diagnostics/language.md#gbs0044-exceptions-are-unavailable) | There is no unwinding machinery on the target. Return a status value instead. |
| Delegates and events | [GBS0045](../reference/diagnostics/language.md#gbs0045-delegates-and-events-are-unavailable) | Require runtime dispatch. Call the target directly, or switch on an enum to choose between behaviours. |
| Interfaces | [GBS0046](../reference/diagnostics/language.md#gbs0046-interfaces-are-unavailable) | Require virtual dispatch. Use a struct with an enum tag and a switch, which lowers to a jump you can see. |
| `async` / `await` | [GBS0047](../reference/diagnostics/language.md#gbs0047-asyncawait-is-unavailable) | There is no scheduler on the target. Drive work from the frame loop instead. |
| Boxing | [GBS0048](../reference/diagnostics/language.md#gbs0048-boxing) | Boxing puts a value on a heap GB# does not have. Keep the value in its own type. |
| LINQ | [GBS0049](../reference/diagnostics/language.md#gbs0049-linq-is-unavailable) | Write the loop. On an 8-bit CPU the loop is what you want to be able to read anyway. |
| Reference type allocation | [GBS0050](../reference/diagnostics/language.md#gbs0050-reference-type-allocation) | `new` on a class needs a heap. Declare it as a struct, or make the type static if it holds no per-instance state. |

The refusal of delegates has a payoff beyond simplicity: with no function pointers, the call graph is the complete account of what can reach what, which is what makes GB#'s call-depth report exact and its bank-layout advice possible.

One construct is legal but warned about rather than refused: recursion ([GBS0058](../reference/diagnostics/language.md#gbs0058-recursive-call)). SM83 has no stack limit check. The stack starts at the top of work RAM and grows down through the same 8 KB the static fields grow up through, so a recursion that goes one level too deep overwrites them: the failure looks like a variable changing value on its own rather than like a crash. Rewriting the recursion as a loop over a `FixedList` is the usual fix, and a program with recursion in it gets no call-depth report, since the depth is whatever the data makes it.

## Fixed collections

`FixedArray<T>` and `FixedList<T>` are the subset's answer to `List<T>`: storage reserved at compile time, at a capacity written in the source. `FixedArray<T>` is a fixed-length array; `FixedList<T>` adds a live `Count`, an `Add` that returns `false` when the list is full (there is nowhere to grow into, so the caller decides what that means), a swap-remove `RemoveAt`, and `Clear`.

```csharp
[Capacity(8)]
private static FixedList<Enemy> enemies;
```

The capacity is compile-time for two reasons. The first is memory: the storage is reserved when the game builds, so the declaration is the complete statement of what the collection costs, and a missing capacity is an error rather than a default ([GBS0054](../reference/diagnostics/language.md#gbs0054-capacity-required); a capacity outside 1–255 is [GBS0055](../reference/diagnostics/language.md#gbs0055-invalid-capacity)). The second is analysis: a `FixedList` refuses to grow past its capacity, so the capacity is a ceiling the compiler can prove, which is what lets the cycle estimator put a total on the most ordinary loop in a GB# program, `for (byte i = 0; i < enemies.Count; i++)`, even though `Count` is a runtime field ([GBS0410](../reference/diagnostics/cycle-cost.md#gbs0410-loop-cost)).

Each distinct element type and capacity specialises into its own emitted C struct, so there is no runtime generic machinery and no indirection. The [Many objects](../tutorials/many-objects.md) tutorial builds a game around one.

## Two deviations from the thesis

Both are places where the thesis's illustrative syntax is not valid C#. GB# keeps the substance and stays real C#: it will not invent a dialect that only looks like C#.

**`Sprites[0].X`** needs `using static GB.Hardware;`. C# has no static indexers, so `Sprites` has to be a value rather than a type for indexing to bind. It costs nothing: the handle types erase entirely, and the whole chain compiles to a single OAM store.

**`FixedList<Enemy, 8>`** is written `[Capacity(8)] static FixedList<Enemy> enemies;`. C# has no value type parameters. The capacity still sits at the declaration, in the source, where you can see what it costs.

## Where the boundary is enforced

The subset is checked twice with one definition. The Roslyn analyzers report these diagnostics in the editor, before any build, and the compiler reports them again when you build; both read `GBSharp.Rules`, so an id means the same thing in both places. `gbsharp analyze` runs the same checks with no C toolchain at all, which makes it the CI lint job: see the [CLI reference](../reference/cli.md).
